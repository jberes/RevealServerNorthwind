using Microsoft.Extensions.Options;
using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.SQLite;

namespace RevealSdk.Sdk
{
    public class DataSourceProvider : IRVDataSourceProvider
    {
        private readonly string _databasePath;

        // Resolve the SQLite file path once. Relative paths (the default
        // "Data/northwind.sqlite") are anchored to the content root so they resolve the
        // same whether the process CWD is the project dir or an Azure App Service root.
        public DataSourceProvider(IOptions<SqliteOptions> options, IWebHostEnvironment env)
        {
            var path = string.IsNullOrWhiteSpace(options.Value.DatabasePath)
                ? "Data/northwind.sqlite"
                : options.Value.DatabasePath;
            _databasePath = Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
        }

        public Task<RVDataSourceItem> ChangeDataSourceItemAsync(IRVUserContext userContext,
            string dashboardId, RVDataSourceItem dataSourceItem)
        {
            if (dataSourceItem is RVSQLiteDataSourceItem sqliteItem)
            {
                // Reveal does NOT carry changes made in ChangeDataSourceAsync into the item,
                // so set the database path on the item's data source here as well.
                ChangeDataSourceAsync(userContext, sqliteItem.DataSource);

                // AI/catalog-generated items pass the table/view name as the item Id when
                // Table isn't set explicitly — fall back to it (matches the old SQL provider).
                if (string.IsNullOrWhiteSpace(sqliteItem.Table) && !string.IsNullOrWhiteSpace(sqliteItem.Id))
                    sqliteItem.Table = sqliteItem.Id;
            }

            return Task.FromResult(dataSourceItem);
        }

        public Task<RVDashboardDataSource> ChangeDataSourceAsync(IRVUserContext userContext,
            RVDashboardDataSource dataSource)
        {
            if (dataSource is RVSQLiteDataSource sqlite)
                sqlite.Database = _databasePath;

            return Task.FromResult(dataSource);
        }
    }
}
