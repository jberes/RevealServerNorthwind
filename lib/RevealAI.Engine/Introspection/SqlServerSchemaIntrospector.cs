using System.Text;
using Microsoft.Data.SqlClient;
using RevealAI.Engine.DataSources;
using RevealAI.Engine.Schema;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Introspection;

/// <summary>
/// Reads table schema, sample rows, AND exact column statistics from SQL Server / Azure SQL using
/// username/password auth. The statistics (distinct counts, null fraction, min/max) let the
/// recommender distinguish measures, identifiers, and good grouping dimensions.
/// </summary>
public sealed class SqlServerSchemaIntrospector : ISchemaIntrospector
{
    public bool CanHandle(ConnectionType type) =>
        type is ConnectionType.SqlServer or ConnectionType.AzureSqlServer;

    public async Task<DatasetSchema> IntrospectAsync(
        ConnectionConfig connection, string dataset, int sampleRows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dataset))
            throw new ArgumentException("A table name (dataset) is required for SQL introspection.");

        var (schemaName, table) = SplitTable(dataset, connection.Schema);
        await using var conn = new SqlConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        var schema = new DatasetSchema { Name = table };
        var lobColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        schema.Columns = await ReadColumnsAsync(conn, schemaName, table, lobColumns, ct);

        if (schema.Columns.Count == 0)
            throw new InvalidOperationException($"Table '{dataset}' was not found or has no columns.");

        if (sampleRows > 0)
            schema.SampleRows = await ReadSampleRowsAsync(conn, schemaName, table, sampleRows, ct);

        foreach (var col in schema.Columns)
            col.SampleValues = schema.SampleRows
                .Select(r => r.TryGetValue(col.Name, out var v) ? v : null)
                .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Take(5).ToList();

        // Exact statistics via a single aggregate query; fall back to sample-based on any error.
        try
        {
            await ProfileExactAsync(conn, schemaName, table, schema, lobColumns, ct);
            schema.StatsAreEstimates = false;
            DataProfiler.Classify(schema);
        }
        catch
        {
            DataProfiler.ProfileFromSampleRows(schema);
        }

        // Tag semantics last, using the (now exact) distinct counts — without changing DB types.
        SchemaInference.TagSemantics(schema);
        return schema;
    }

    public async Task<List<string>> ListDatasetsAsync(ConnectionConfig connection, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        await using var cmd = new SqlCommand(sql, conn);
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            tables.Add(string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}");
        }
        return tables;
    }

    private static string BuildConnectionString(ConnectionConfig c)
    {
        var sb = new SqlConnectionStringBuilder
        {
            DataSource = c.Host ?? "localhost",
            InitialCatalog = c.Database ?? "",
            UserID = c.Username ?? "",
            Password = c.Password ?? "",
            TrustServerCertificate = true,
            ConnectTimeout = 15
        };
        if (c.Port is int port && c.Host is not null && !c.Host.Contains(','))
            sb.DataSource = $"{c.Host},{port}";
        return sb.ConnectionString;
    }

    private static async Task<List<ColumnSchema>> ReadColumnsAsync(
        SqlConnection conn, string? schemaName, string table, HashSet<string> lobColumns, CancellationToken ct)
    {
        const string sql = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @table AND (@schema IS NULL OR TABLE_SCHEMA = @schema)
            ORDER BY ORDINAL_POSITION
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@schema", (object?)schemaName ?? DBNull.Value);

        var columns = new List<ColumnSchema>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var sqlType = reader.GetString(1).ToLowerInvariant();
            var maxLen = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);

            if (IsLob(sqlType, maxLen)) lobColumns.Add(name);

            columns.Add(new ColumnSchema
            {
                Name = name,
                DataType = MapSqlType(sqlType),
                Nullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                IsInteger = sqlType is "int" or "bigint" or "smallint" or "tinyint"
            });
        }
        return columns;
    }

    /// <summary>One aggregate query: COUNT(*), and per profileable column distinct/non-null + min/max.</summary>
    private static async Task ProfileExactAsync(
        SqlConnection conn, string? schemaName, string table, DatasetSchema schema,
        HashSet<string> lobColumns, CancellationToken ct)
    {
        var profileable = schema.Columns.Where(c => !lobColumns.Contains(c.Name)).ToList();
        var qualified = schemaName is null ? $"[{Escape(table)}]" : $"[{Escape(schemaName)}].[{Escape(table)}]";

        var sb = new StringBuilder("SELECT COUNT_BIG(*) AS r");
        for (int i = 0; i < profileable.Count; i++)
        {
            var col = Escape(profileable[i].Name);
            sb.Append($", COUNT_BIG(DISTINCT [{col}]) AS d{i}, COUNT_BIG([{col}]) AS n{i}");
            if (profileable[i].DataType is DataType.Number or DataType.Date or DataType.DateTime)
                sb.Append($", MIN([{col}]) AS mn{i}, MAX([{col}]) AS mx{i}");
        }
        sb.Append($" FROM {qualified}");

        await using var cmd = new SqlCommand(sb.ToString(), conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return;

        var rowCount = reader.GetInt64(reader.GetOrdinal("r"));
        schema.RowCount = rowCount;

        for (int i = 0; i < profileable.Count; i++)
        {
            var col = profileable[i];
            var distinct = reader.GetInt64(reader.GetOrdinal($"d{i}"));
            var nonNull = reader.GetInt64(reader.GetOrdinal($"n{i}"));
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

    private static string? ReadValue(SqlDataReader reader, string alias)
    {
        var ord = reader.GetOrdinal(alias);
        if (reader.IsDBNull(ord)) return null;
        var value = reader.GetValue(ord);
        return value is DateTime dt ? dt.ToString("yyyy-MM-dd") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<Dictionary<string, string?>>> ReadSampleRowsAsync(
        SqlConnection conn, string? schemaName, string table, int n, CancellationToken ct)
    {
        var qualified = schemaName is null ? $"[{Escape(table)}]" : $"[{Escape(schemaName)}].[{Escape(table)}]";
        await using var cmd = new SqlCommand($"SELECT TOP ({n}) * FROM {qualified}", conn) { CommandTimeout = 60 };

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

    private static (string? schema, string table) SplitTable(string dataset, string? defaultSchema)
    {
        var parts = dataset.Split('.', 2);
        return parts.Length == 2
            ? (parts[0].Trim('[', ']'), parts[1].Trim('[', ']'))
            : (string.IsNullOrWhiteSpace(defaultSchema) ? null : defaultSchema, dataset.Trim('[', ']'));
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]");

    private static bool IsLob(string sqlType, int? maxLen) =>
        sqlType is "text" or "ntext" or "image" or "xml" or "geography" or "geometry"
            or "hierarchyid" or "varbinary" or "binary" or "sql_variant"
        || maxLen == -1; // varchar(max) / nvarchar(max)

    private static DataType MapSqlType(string sqlType) => sqlType switch
    {
        "bit" => DataType.Boolean,
        "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric"
            or "float" or "real" or "money" or "smallmoney" => DataType.Number,
        "date" => DataType.Date,
        "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => DataType.DateTime,
        _ => DataType.Text
    };
}
