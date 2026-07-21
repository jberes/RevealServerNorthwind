using Microsoft.Data.Sqlite;
using RevealAI.Engine.DataSources;
using RevealAI.Engine.Schema;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Introspection;

/// <summary>
/// Reads table/view schema, sample rows, and column statistics from a SQLite database file.
/// SQLite is a local file with no schema or credentials — <see cref="ConnectionConfig.Database"/>
/// holds the file path. Column types come from PRAGMA table_info; because views (and loosely-typed
/// columns) can report no declared type, we re-infer from sample values before profiling.
/// </summary>
public sealed class SqliteSchemaIntrospector : ISchemaIntrospector
{
    public bool CanHandle(ConnectionType type) => type is ConnectionType.Sqlite;

    public async Task<DatasetSchema> IntrospectAsync(
        ConnectionConfig connection, string dataset, int sampleRows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dataset))
            throw new ArgumentException("A table/view name (dataset) is required for SQLite introspection.");

        var table = dataset.Trim().Trim('[', ']', '"');
        await using var conn = new SqliteConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        var schema = new DatasetSchema { Name = table };
        var lobColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Columns with NO declared type (computed view columns) — only these are re-inferred
        // from sample values. Declared columns keep their mapped type so numeric-looking text
        // (PostalCode, Extension) stays Text.
        var untypedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        schema.Columns = await ReadColumnsAsync(conn, table, lobColumns, untypedColumns, ct);

        if (schema.Columns.Count == 0)
            throw new InvalidOperationException($"Object '{dataset}' was not found or has no columns.");

        if (sampleRows > 0)
            schema.SampleRows = await ReadSampleRowsAsync(conn, table, sampleRows, ct);

        foreach (var col in schema.Columns)
            col.SampleValues = schema.SampleRows
                .Select(r => r.TryGetValue(col.Name, out var v) ? v : null)
                .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Take(5).ToList();

        // SQLite view columns often report no declared type — re-infer ONLY those from sample
        // values (not declared text columns) so computed numeric/date columns are recognised
        // without mis-promoting numeric-looking text like PostalCode/Extension.
        SchemaInference.EnrichOnly(schema, untypedColumns);

        // Exact statistics via a single aggregate query; fall back to sample-based on any error.
        try
        {
            await ProfileExactAsync(conn, table, schema, lobColumns, ct);
            schema.StatsAreEstimates = false;
            DataProfiler.Classify(schema);
        }
        catch
        {
            DataProfiler.ProfileFromSampleRows(schema);
        }

        SchemaInference.TagSemantics(schema);
        return schema;
    }

    public async Task<List<string>> ListDatasetsAsync(ConnectionConfig connection, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT name FROM sqlite_master
                            WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'
                            ORDER BY name";
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static string BuildConnectionString(ConnectionConfig c) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = c.Database ?? "",
            Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString;

    private static async Task<List<ColumnSchema>> ReadColumnsAsync(
        SqliteConnection conn, string table, HashSet<string> lobColumns, HashSet<string> untypedColumns, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // PRAGMA table_info works for both tables and views. Parameterised via a literal
        // is not supported for PRAGMA, so quote the (catalog-verified) name.
        cmd.CommandText = $"PRAGMA table_info(\"{Escape(table)}\")";

        var columns = new List<ColumnSchema>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(1);                     // 1 = name
            var declType = reader.IsDBNull(2) ? "" : reader.GetString(2); // 2 = type
            var notNull = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;  // 3 = notnull
            var affinity = declType.ToUpperInvariant();

            if (IsLob(affinity)) lobColumns.Add(name);
            if (string.IsNullOrWhiteSpace(affinity)) untypedColumns.Add(name);

            columns.Add(new ColumnSchema
            {
                Name = name,
                DataType = MapSqliteType(affinity),
                Nullable = !notNull,
                IsInteger = affinity.Contains("INT")
            });
        }
        return columns;
    }

    /// <summary>One aggregate query: COUNT(*), and per profileable column distinct/non-null + min/max.</summary>
    private static async Task ProfileExactAsync(
        SqliteConnection conn, string table, DatasetSchema schema, HashSet<string> lobColumns, CancellationToken ct)
    {
        var profileable = schema.Columns.Where(c => !lobColumns.Contains(c.Name)).ToList();
        var qualified = $"\"{Escape(table)}\"";

        var sb = new System.Text.StringBuilder("SELECT COUNT(*) AS r");
        for (int i = 0; i < profileable.Count; i++)
        {
            var col = Escape(profileable[i].Name);
            sb.Append($", COUNT(DISTINCT \"{col}\") AS d{i}, COUNT(\"{col}\") AS n{i}");
            if (profileable[i].DataType is DataType.Number or DataType.Date or DataType.DateTime)
                sb.Append($", MIN(\"{col}\") AS mn{i}, MAX(\"{col}\") AS mx{i}");
        }
        sb.Append($" FROM {qualified}");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return;

        var rowCount = Convert.ToInt64(reader.GetValue(reader.GetOrdinal("r")));
        schema.RowCount = rowCount;

        for (int i = 0; i < profileable.Count; i++)
        {
            var col = profileable[i];
            var distinct = Convert.ToInt64(reader.GetValue(reader.GetOrdinal($"d{i}")));
            var nonNull = Convert.ToInt64(reader.GetValue(reader.GetOrdinal($"n{i}")));
            col.DistinctCount = (int)Math.Min(distinct, int.MaxValue);
            col.NonNullCount = nonNull;
            col.NullFraction = rowCount > 0 ? (double)(rowCount - nonNull) / rowCount : null;

            if (col.DataType is DataType.Number or DataType.Date or DataType.DateTime)
            {
                col.Min = ReadValue(reader, $"mn{i}");
                col.Max = ReadValue(reader, $"mx{i}");
            }
        }
    }

    private static string? ReadValue(SqliteDataReader reader, string alias)
    {
        var ord = reader.GetOrdinal(alias);
        if (reader.IsDBNull(ord)) return null;
        var value = reader.GetValue(ord);
        return value is DateTime dt
            ? dt.ToString("yyyy-MM-dd")
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<Dictionary<string, string?>>> ReadSampleRowsAsync(
        SqliteConnection conn, string table, int n, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{Escape(table)}\" LIMIT {n}";

        var rows = new List<Dictionary<string, string?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
            rows.Add(dict);
        }
        return rows;
    }

    private static string Escape(string identifier) => identifier.Replace("\"", "\"\"");

    private static bool IsLob(string affinity) => affinity.Contains("BLOB") || affinity.Contains("IMAGE");

    // SQLite type affinity is fuzzy — match on substrings of the declared type.
    private static DataType MapSqliteType(string affinity)
    {
        if (string.IsNullOrEmpty(affinity)) return DataType.Text;
        if (affinity.Contains("INT")) return DataType.Number;
        if (affinity.Contains("CHAR") || affinity.Contains("CLOB") || affinity.Contains("TEXT")) return DataType.Text;
        if (affinity.Contains("REAL") || affinity.Contains("FLOA") || affinity.Contains("DOUB")
            || affinity.Contains("NUMERIC") || affinity.Contains("DECIMAL") || affinity.Contains("MONEY")) return DataType.Number;
        if (affinity.Contains("BOOL") || affinity == "BIT") return DataType.Boolean;
        if (affinity.Contains("DATETIME") || affinity.Contains("TIMESTAMP")) return DataType.DateTime;
        if (affinity.Contains("DATE") || affinity.Contains("TIME")) return DataType.Date;
        return DataType.Text;
    }
}
