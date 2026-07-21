using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Microsoft.SqlServer;

namespace RevealSdk.Sdk
{
    public class DataSourceProvider : IRVDataSourceProvider
    {
        public Task<RVDataSourceItem> ChangeDataSourceItemAsync(IRVUserContext userContext,
            string dashboardId, RVDataSourceItem dataSourceItem)
        {
            if (dataSourceItem is RVSqlServerDataSourceItem sqlServerDsi)
            {
                ChangeDataSourceAsync(userContext, sqlServerDsi.DataSource);

                // Treat the incoming item's Table as the object to query. If Table
                // isn't set, fall back to the item Id (the AI/catalog passes the
                // table/view name as the datasource item identifier).
                var table = !string.IsNullOrWhiteSpace(sqlServerDsi.Table)
                    ? sqlServerDsi.Table
                    : sqlServerDsi.Id;

                if (!string.IsNullOrWhiteSpace(table))
                {
                    sqlServerDsi.Table = table;
                    sqlServerDsi.CustomQuery = $"SELECT * FROM [{table}]";
                }

                return Task.FromResult(dataSourceItem);
            }

            ChangeDataSourceAsync(userContext, dataSourceItem.DataSource);

            return Task.FromResult(dataSourceItem);
        }

        public Task<RVDashboardDataSource> ChangeDataSourceAsync(IRVUserContext userContext, RVDashboardDataSource dataSource)
        {
            if (dataSource is RVSqlServerDataSource sqlServerDataSource)
            {
                var host = userContext.Properties.TryGetValue("Host", out var hostValue)
                    ? hostValue?.ToString()
                    : null;
                var database = userContext.Properties.TryGetValue("Database", out var databaseValue)
                    ? databaseValue?.ToString()
                    : null;
                var schema = userContext.Properties.TryGetValue("Schema", out var schemaValue)
                    ? schemaValue?.ToString()
                    : null;
                var trustServerCertificate = userContext.Properties.TryGetValue("TrustServerCertificate", out var trustValue)
                    && bool.TryParse(trustValue?.ToString(), out var contextTrustServerCertificate)
                        ? contextTrustServerCertificate
                        : true;

                if (!string.IsNullOrWhiteSpace(host))
                {
                    sqlServerDataSource.Host = host;
                }

                if (!string.IsNullOrWhiteSpace(database))
                {
                    sqlServerDataSource.Database = database;
                }

                if (!string.IsNullOrWhiteSpace(schema))
                {
                    sqlServerDataSource.Schema = schema;
                }

                sqlServerDataSource.TrustServerCertificate = trustServerCertificate;
            }

            return Task.FromResult(dataSource);
        }
    }
}
