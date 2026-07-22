using Microsoft.Data.SqlClient;

namespace RevealSdk.Services
{
    /// <summary>
    /// SQL Server counterpart of the SQLite browsing helpers: object listing, row
    /// counts, column discovery, and grid preview data for config-defined SQL
    /// Server sources. Scoped to the source's configured schema (default dbo).
    /// </summary>
    public static class SqlServerCatalog
    {
        public sealed record SqlObjectInfo(string Name, string Type);

        public static async Task<List<SqlObjectInfo>> ListObjectsAsync(
            SqlServerSourceConfig cfg, CancellationToken ct = default)
        {
            var list = new List<SqlObjectInfo>();
            await using var conn = new SqlConnection(cfg.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT t.name, 'table' AS type
                FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = @schema
                UNION ALL
                SELECT v.name, 'view' AS type
                FROM sys.views v JOIN sys.schemas s ON v.schema_id = s.schema_id
                WHERE s.name = @schema
                ORDER BY type, name
                """;
            cmd.Parameters.AddWithValue("@schema", cfg.Schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new SqlObjectInfo(r.GetString(0), r.GetString(1)));
            return list;
        }

        /// <summary>Fast approximate row counts for tables (partition stats); views are skipped.</summary>
        public static async Task<Dictionary<string, long>> RowCountsAsync(
            SqlServerSourceConfig cfg, CancellationToken ct = default)
        {
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using var conn = new SqlConnection(cfg.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT t.name, SUM(p.rows)
                FROM sys.tables t
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                WHERE s.name = @schema
                GROUP BY t.name
                """;
            cmd.Parameters.AddWithValue("@schema", cfg.Schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                counts[r.GetString(0)] = r.GetInt64(1);
            return counts;
        }

        public static async Task<Dictionary<string, List<(string Name, string Type)>>> DiscoverColumnsAsync(
            SqlServerSourceConfig cfg, string[] names, CancellationToken ct = default)
        {
            var result = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names) result[n] = new List<(string, string)>();
            if (names.Length == 0) return result;

            await using var conn = new SqlConnection(cfg.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @schema
                ORDER BY TABLE_NAME, ORDINAL_POSITION
                """;
            cmd.Parameters.AddWithValue("@schema", cfg.Schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var table = r.GetString(0);
                if (result.TryGetValue(table, out var cols))
                    cols.Add((r.GetString(1), r.GetString(2)));
            }
            return result;
        }

        /// <summary>TOP-N preview for the Connections grid, columns typed by CLR type.</summary>
        public static async Task<(List<object> Columns, List<Dictionary<string, object?>> Rows)> GetDataAsync(
            SqlServerSourceConfig cfg, string objectName, int top, CancellationToken ct = default)
        {
            // Validate the name against the live catalog — never interpolate raw input.
            var objects = await ListObjectsAsync(cfg, ct);
            if (!objects.Any(o => string.Equals(o.Name, objectName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"'{objectName}' is not a table or view in {cfg.Database}.{cfg.Schema}.");

            await using var conn = new SqlConnection(cfg.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT TOP ({top}) * FROM [{cfg.Schema}].[{objectName.Replace("]", "]]")}]";
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var columns = new List<object>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(new { name = reader.GetName(i), type = reader.GetFieldType(i).Name });

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return (columns, rows);
        }
    }
}
