using Reveal.Sdk.Dom.Data;
using Reveal.Sdk.Dom.Visualizations;
using RevealAI.Engine.DataSources;
using RevealAI.Engine.Schema;
using EngineDataType = RevealAI.Engine.Spec.DataType;

namespace RevealAI.Engine.Compilation;

/// <summary>
/// Turns a <see cref="ConnectionConfig"/> + <see cref="DatasetSchema"/> into a Reveal
/// <c>DataSourceItem</c> with typed fields. This is the only place that knows about specific
/// Reveal connector classes; add a case here to support a new connection type.
///
/// Note: credentials (username/password) are intentionally NOT written here — Reveal resolves them
/// server-side via its data-source credential provider. They live in <see cref="ConnectionConfig"/>
/// for that provider to consume.
/// </summary>
public sealed class DataSourceResolver
{
    public DataSourceItem Resolve(ConnectionConfig conn, DatasetSchema schema, string dataset)
    {
        var fields = BuildFields(schema);
        var title = string.IsNullOrWhiteSpace(schema.Name) ? conn.Title : schema.Name;

        DataSourceItem item = conn.Type switch
        {
            ConnectionType.SqlServer => Database(
                new MicrosoftSqlServerDataSource { Host = conn.Host, Database = conn.Database },
                ds => new MicrosoftSqlServerDataSourceItem(title, (MicrosoftSqlServerDataSource)ds), conn, dataset),

            ConnectionType.AzureSqlServer => Database(
                new MicrosoftAzureSqlServerDataSource { Host = conn.Host, Database = conn.Database },
                ds => new MicrosoftAzureSqlServerDataSourceItem(title, (MicrosoftAzureSqlServerDataSource)ds), conn, dataset),

            ConnectionType.PostgreSql => Database(
                new PostgreSQLDataSource { Host = conn.Host, Database = conn.Database },
                ds => new PostgreSqlDataSourceItem(title, (PostgreSQLDataSource)ds), conn, dataset),

            ConnectionType.MySql => Database(
                new MySqlDataSource { Host = conn.Host, Database = conn.Database },
                ds => new MySqlDataSourceItem(title, (MySqlDataSource)ds), conn, dataset),

            ConnectionType.Oracle => Database(
                new OracleDataSource { Host = conn.Host },
                ds => new OracleDataSourceItem(title, ds), conn, dataset),

            ConnectionType.AmazonRedshift => Database(
                new AmazonRedshiftDataSource { Host = conn.Host, Database = conn.Database },
                ds => new AmazonRedshiftDataSourceItem(title, (AmazonRedshiftDataSource)ds), conn, dataset),

            ConnectionType.Snowflake => Database(
                new SnowflakeDataSource { Host = conn.Host, Database = conn.Database },
                ds => new SnowflakeDataSourceItem(title, (SnowflakeDataSource)ds), conn, dataset),

            ConnectionType.Excel => Rest(conn, title, dataset, FileKind.Excel),
            ConnectionType.Csv => Rest(conn, title, dataset, FileKind.Csv),
            ConnectionType.Rest => Rest(conn, title, dataset, FileKind.Json),

            _ => throw new NotSupportedException($"Connection type '{conn.Type}' is not supported yet.")
        };

        item.Fields = fields;
        return item;
    }

    private enum FileKind { Json, Excel, Csv }

    /// <summary>Build a database-backed item, setting host/port/db/schema + the table name + credentials' host bits.</summary>
    private static DataSourceItem Database(
        DataSource dataSource, Func<DataSource, DataSourceItem> makeItem, ConnectionConfig conn, string dataset)
    {
        dataSource.Id = conn.Id;
        dataSource.Title = string.IsNullOrWhiteSpace(conn.Title) ? conn.Id : conn.Title;
        if (conn.Port is int port && dataSource is HostDataSource hds) hds.Port = port;
        if (!string.IsNullOrWhiteSpace(conn.Schema) && dataSource is SchemaDataSource sds) sds.Schema = conn.Schema!;

        var item = makeItem(dataSource);

        // Server-side processing enables grid paging and pushes aggregation down to the database.
        if (item is IProcessDataOnServer process)
            process.ProcessDataOnServer = true;

        if (item is TableDataSourceItem table && !string.IsNullOrWhiteSpace(dataset))
            table.Table = dataset;
        if (item is DatabaseDataSourceItem dbItem && !string.IsNullOrWhiteSpace(conn.Database))
            dbItem.Database = conn.Database!;
        if (item is SchemaDataSourceItem schemaItem && !string.IsNullOrWhiteSpace(conn.Schema))
            schemaItem.Schema = conn.Schema!;

        return item;
    }

    private static DataSourceItem Rest(ConnectionConfig conn, string title, string dataset, FileKind kind)
    {
        if (string.IsNullOrWhiteSpace(conn.Url))
            throw new InvalidOperationException($"Connection '{conn.Id}' of type {conn.Type} requires a Url.");

        var ds = new DataSource { Id = conn.Id, Title = string.IsNullOrWhiteSpace(conn.Title) ? conn.Id : conn.Title };
        var item = new RestDataSourceItem(title, conn.Url!, ds) { IsAnonymous = conn.IsAnonymous };

        switch (kind)
        {
            case FileKind.Excel:
                if (!string.IsNullOrWhiteSpace(dataset)) item.WithExcel(dataset);
                break;
            case FileKind.Csv:
                item.WithCsv();
                break;
        }
        return item;
    }

    private static List<IField> BuildFields(DatasetSchema schema) =>
        schema.Columns.Select<ColumnSchema, IField>(c => c.DataType switch
        {
            // Large Number Formatting = Auto on numeric fields (also covers grid columns).
            EngineDataType.Number => new NumberField(c.Name)
            {
                Formatting = new NumberFormatting { LargeNumberFormat = LargeNumberFormat.Auto }
            },
            EngineDataType.Date => new DateField(c.Name),
            EngineDataType.DateTime => new DateTimeField(c.Name),
            _ => new TextField(c.Name)
        }).ToList();
}
