using Microsoft.Data.SqlClient;
using RevealAI.Engine.DataSources;
using RevealAI.Engine.Schema;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Introspection;

/// <summary>
/// Reads table/view schema, sample rows, and column statistics from SQL Server
/// (including Azure SQL). Column types come from INFORMATION_SCHEMA; profiling
/// mirrors the SQLite introspector (one aggregate query, sample fallback).
/// </summary>
public sealed class SqlServerSchemaIntrospector : ISchemaIntrospector
{
    public bool CanHandle(ConnectionType type) => type is ConnectionType.SqlServer or ConnectionType.AzureSqlServer;

    public async Task<DatasetSchema> IntrospectAsync(
        ConnectionConfig connection, string dataset, int sampleRows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dataset))
            throw new ArgumentException("A table/view name (dataset) is required for SQL Server introspection.");

        var table = dataset.Trim().Trim('[', ']', '"');
        var schemaName = string.IsNullOrWhiteSpace(connection.Schema) ? "dbo" : connection.Schema!;
        await using var conn = new SqlConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        var schema = new DatasetSchema { Name = table };
        var lobColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        schema.Columns = await ReadColumnsAsync(conn, schemaName, table, lobColumns, ct);

        if (schema.Columns.Count == 0)
            throw new InvalidOperationException($"Object '{dataset}' was not found or has no columns.");

        if (sampleRows > 0)
            schema.SampleRows = await ReadSampleRowsAsync(conn, schemaName, table, sampleRows, ct);

        foreach (var col in schema.Columns)
            col.SampleValues = schema.SampleRows
                .Select(r => r.TryGetValue(col.Name, out var v) ? v : null)
                .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Take(5).ToList();

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

        SchemaInference.TagSemantics(schema);
        return schema;
    }

    public async Task<List<string>> ListDatasetsAsync(ConnectionConfig connection, CancellationToken ct = default)
    {
        var schemaName = string.IsNullOrWhiteSpace(connection.Schema) ? "dbo" : connection.Schema!;
        await using var conn = new SqlConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = @schema
            UNION ALL
            SELECT v.name FROM sys.views v JOIN sys.schemas s ON v.schema_id = s.schema_id WHERE s.name = @schema
            ORDER BY name
            """;
        cmd.Parameters.AddWithValue("@schema", schemaName);
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static string BuildConnectionString(ConnectionConfig c) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{c.Host},{c.Port ?? 1433}",
            InitialCatalog = c.Database ?? "",
            UserID = c.Username ?? "",
            Password = c.Password ?? "",
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 30
        }.ConnectionString;

    private static async Task<List<ColumnSchema>> ReadColumnsAsync(
        SqlConnection conn, string schemaName, string table, HashSet<string> lobColumns, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@table", table);

        var columns = new List<ColumnSchema>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var sqlType = reader.GetString(1).ToUpperInvariant();
            var nullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase);

            if (IsLob(sqlType)) lobColumns.Add(name);

            columns.Add(new ColumnSchema
            {
                Name = name,
                DataType = MapSqlType(sqlType),
                Nullable = nullable,
                IsInteger = sqlType is "INT" or "BIGINT" or "SMALLINT" or "TINYINT"
            });
        }
        return columns;
    }

    private static async Task ProfileExactAsync(
        SqlConnection conn, string schemaName, string table, DatasetSchema schema,
        HashSet<string> lobColumns, CancellationToken ct)
    {
        var profileable = schema.Columns.Where(c => !lobColumns.Contains(c.Name)).ToList();
        var qualified = $"[{Escape(schemaName)}].[{Escape(table)}]";

        var sb = new System.Text.StringBuilder("SELECT COUNT_BIG(*) AS r");
        for (int i = 0; i < profileable.Count; i++)
        {
            var col = Escape(profileable[i].Name);
            sb.Append($", COUNT_BIG(DISTINCT [{col}]) AS d{i}, COUNT_BIG([{col}]) AS n{i}");
            if (profileable[i].DataType is DataType.Number or DataType.Date or DataType.DateTime)
                sb.Append($", MIN([{col}]) AS mn{i}, MAX([{col}]) AS mx{i}");
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

    private static string? ReadValue(SqlDataReader reader, string alias)
    {
        var ord = reader.GetOrdinal(alias);
        if (reader.IsDBNull(ord)) return null;
        var value = reader.GetValue(ord);
        return value is DateTime dt
            ? dt.ToString("yyyy-MM-dd")
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<Dictionary<string, string?>>> ReadSampleRowsAsync(
        SqlConnection conn, string schemaName, string table, int n, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TOP ({n}) * FROM [{Escape(schemaName)}].[{Escape(table)}]";

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

    private static string Escape(string identifier) => identifier.Replace("]", "]]");

    private static bool IsLob(string sqlType) =>
        sqlType is "TEXT" or "NTEXT" or "IMAGE" or "VARBINARY" or "BINARY" or "XML";

    private static DataType MapSqlType(string sqlType) => sqlType switch
    {
        "INT" or "BIGINT" or "SMALLINT" or "TINYINT" or "DECIMAL" or "NUMERIC"
            or "FLOAT" or "REAL" or "MONEY" or "SMALLMONEY" => DataType.Number,
        "BIT" => DataType.Boolean,
        "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "DATETIMEOFFSET" => DataType.DateTime,
        "DATE" => DataType.Date,
        "TIME" => DataType.Date,
        _ => DataType.Text
    };
}
