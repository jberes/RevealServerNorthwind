using System.Data;
using System.Globalization;
using System.Text;
using ExcelDataReader;
using Microsoft.Data.Sqlite;

namespace RevealSdk.Services
{
    public sealed record ImportedTable(string Name, int Rows, IReadOnlyList<string> Columns);
    public sealed record ImportResult(List<ImportedTable> Tables, List<string> Warnings);

    /// <summary>
    /// Imports an Excel workbook into a SQLite database: every worksheet tab becomes a
    /// table (tab name = table name, first non-empty row = column headers).
    ///
    /// SQLite conventions required by Reveal's SQLite connector (hard-won):
    ///  - date/datetime values are stored as INTEGER Unix epoch SECONDS (its reader does
    ///    (long)value and its date SQL uses strftime('%s', col, 'unixepoch'));
    ///  - declared column types are clean strings ("integer", "real", "datetime",
    ///    "nvarchar(255)") so the connector maps .NET types correctly.
    /// </summary>
    public sealed class ExcelToSqliteImporter
    {
        static ExcelToSqliteImporter()
        {
            // ExcelDataReader needs legacy code pages to parse .xls files.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private enum ColKind { Unknown, Integer, Real, DateTime, DateOnly, Bool, Text }

        public Task<ImportResult> ImportAsync(string workbookPath, string sqlitePath, CancellationToken ct = default)
            => Task.Run(() => Import(workbookPath, sqlitePath, ct), ct);

        private static ImportResult Import(string workbookPath, string sqlitePath, CancellationToken ct)
        {
            var warnings = new List<string>();
            var tables = new List<ImportedTable>();
            var usedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var stream = File.Open(workbookPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ConnectionString);
            conn.Open();

            do
            {
                ct.ThrowIfCancellationRequested();
                var sheetName = reader.Name ?? $"Sheet{tables.Count + 1}";

                // ---- Buffer the sheet (demo scale; forward-only reader needs two passes) ----
                var rows = new List<object?[]>();
                var maxWidth = 0;
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var v = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        if (v is string s) v = s.Trim().Length == 0 ? null : s.Trim();
                        row[i] = v;
                    }
                    rows.Add(row);
                    maxWidth = Math.Max(maxWidth, row.Length);
                }

                // ---- Header = first row with any non-null cell ----
                var headerIdx = rows.FindIndex(r => r.Any(c => c is not null));
                if (headerIdx < 0)
                {
                    warnings.Add($"Sheet '{sheetName}' is empty — skipped.");
                    continue;
                }
                var headerRow = rows[headerIdx];
                var dataRows = rows.Skip(headerIdx + 1).ToList();
                if (dataRows.Count == 0)
                {
                    warnings.Add($"Sheet '{sheetName}' has a header but no data rows — skipped.");
                    continue;
                }

                // ---- Column names: sanitize, fill blanks, dedupe ----
                var colNames = new List<string>();
                var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headerRow.Length; i++)
                {
                    var raw = Convert.ToString(headerRow[i], CultureInfo.InvariantCulture);
                    var name = string.IsNullOrWhiteSpace(raw) ? $"Column{i + 1}" : SourceRegistry.Sanitize(raw!);
                    var baseName = name;
                    for (int n = 2; !seenCols.Add(name); n++) name = $"{baseName}_{n}";
                    colNames.Add(name);
                }
                var width = colNames.Count;

                // ---- Type inference per column ----
                var kinds = new ColKind[width];
                foreach (var row in dataRows)
                {
                    for (int i = 0; i < width; i++)
                    {
                        var v = i < row.Length ? row[i] : null;
                        if (v is null) continue;
                        kinds[i] = Merge(kinds[i], Classify(v));
                    }
                }
                for (int i = 0; i < width; i++)
                    if (kinds[i] == ColKind.Unknown) kinds[i] = ColKind.Text; // all-null column

                // Ragged-row warning (once per sheet).
                if (dataRows.Any(r => r.Count(c => c is not null) > width))
                    warnings.Add($"Sheet '{sheetName}': some rows have more cells than the header — extras ignored.");

                // ---- Table name ----
                var tableName = SourceRegistry.Sanitize(sheetName);
                var tableBase = tableName;
                for (int n = 2; !usedTableNames.Add(tableName); n++) tableName = $"{tableBase}_{n}";
                if (tableName != sheetName)
                    warnings.Add($"Sheet '{sheetName}' imported as table '{tableName}'.");

                // ---- DDL + bulk insert ----
                var ddlCols = string.Join(", ", colNames.Select((c, i) => $"\"{Esc(c)}\" {DeclType(kinds[i])}"));
                using (var create = conn.CreateCommand())
                {
                    create.CommandText = $"CREATE TABLE \"{Esc(tableName)}\" ({ddlCols})";
                    create.ExecuteNonQuery();
                }

                using (var tx = conn.BeginTransaction())
                {
                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText =
                        $"INSERT INTO \"{Esc(tableName)}\" VALUES ({string.Join(", ", colNames.Select((_, i) => $"$p{i}"))})";
                    var ps = new SqliteParameter[width];
                    for (int i = 0; i < width; i++)
                    {
                        ps[i] = insert.CreateParameter();
                        ps[i].ParameterName = $"$p{i}";
                        insert.Parameters.Add(ps[i]);
                    }

                    foreach (var row in dataRows)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (row.All(c => c is null)) continue; // skip fully blank rows
                        for (int i = 0; i < width; i++)
                        {
                            var v = i < row.Length ? row[i] : null;
                            ps[i].Value = ToDbValue(v, kinds[i]) ?? DBNull.Value;
                        }
                        insert.ExecuteNonQuery();
                    }
                    tx.Commit();
                }

                int rowCount;
                using (var count = conn.CreateCommand())
                {
                    count.CommandText = $"SELECT COUNT(*) FROM \"{Esc(tableName)}\"";
                    rowCount = Convert.ToInt32(count.ExecuteScalar());
                }
                tables.Add(new ImportedTable(tableName, rowCount, colNames));
            }
            while (reader.NextResult());

            if (tables.Count == 0)
                throw new InvalidOperationException("The workbook contains no importable sheets (all empty).");

            return new ImportResult(tables, warnings);
        }

        // ---- type lattice -------------------------------------------------------

        private static ColKind Classify(object v) => v switch
        {
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? ColKind.DateOnly : ColKind.DateTime,
            bool => ColKind.Bool,
            double d => d == Math.Truncate(d) && d is >= long.MinValue and <= long.MaxValue
                ? ColKind.Integer : ColKind.Real,
            float f => f == Math.Truncate(f) ? ColKind.Integer : ColKind.Real,
            decimal m => m == Math.Truncate(m) ? ColKind.Integer : ColKind.Real,
            int or long or short or byte => ColKind.Integer,
            // A numeric-LOOKING string does NOT promote — strings force TEXT
            // (protects zip codes / phone numbers / leading zeros).
            _ => ColKind.Text
        };

        private static ColKind Merge(ColKind a, ColKind b)
        {
            if (a == ColKind.Unknown) return b;
            if (a == b) return a;
            if (a == ColKind.Text || b == ColKind.Text) return ColKind.Text;
            // date + non-date mix → text (safest to display)
            var aDate = a is ColKind.DateTime or ColKind.DateOnly;
            var bDate = b is ColKind.DateTime or ColKind.DateOnly;
            if (aDate && bDate) return ColKind.DateTime;
            if (aDate || bDate) return ColKind.Text;
            // bool + numeric → integer; integer + real → real
            if (a == ColKind.Bool || b == ColKind.Bool) return ColKind.Integer;
            return ColKind.Real;
        }

        private static string DeclType(ColKind k) => k switch
        {
            ColKind.Integer or ColKind.Bool => "integer",
            ColKind.Real => "real",
            ColKind.DateTime => "datetime",
            ColKind.DateOnly => "date",
            _ => "nvarchar(255)"
        };

        private static object? ToDbValue(object? v, ColKind k)
        {
            if (v is null) return null;
            switch (k)
            {
                case ColKind.DateTime:
                case ColKind.DateOnly:
                    // Excel dates are timezone-naive — treat as UTC so the stored epoch
                    // round-trips Reveal's strftime('%s', col, 'unixepoch') exactly.
                    return v is DateTime dt
                        ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds()
                        : null;
                case ColKind.Integer:
                case ColKind.Bool:
                    return v switch
                    {
                        bool b => b ? 1L : 0L,
                        double d => (long)d,
                        float f => (long)f,
                        decimal m => (long)m,
                        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture)
                    };
                case ColKind.Real:
                    return Convert.ToDouble(v, CultureInfo.InvariantCulture);
                default:
                    return v is DateTime tdt
                        ? tdt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        private static string Esc(string identifier) => identifier.Replace("\"", "\"\"");
    }
}
