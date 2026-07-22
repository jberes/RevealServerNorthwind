using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.SQLite;
using RevealSdk.Services;

namespace RevealSdk.Sdk
{
    public class DataSourceProvider : IRVDataSourceProvider
    {
        private readonly SourceRegistry _registry;

        public DataSourceProvider(SourceRegistry registry)
        {
            _registry = registry;
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
                // Table isn't set explicitly — fall back to it.
                if (string.IsNullOrWhiteSpace(sqliteItem.Table) && !string.IsNullOrWhiteSpace(sqliteItem.Id))
                    sqliteItem.Table = sqliteItem.Id;
            }

            return Task.FromResult(dataSourceItem);
        }

        public Task<RVDashboardDataSource> ChangeDataSourceAsync(IRVUserContext userContext,
            RVDashboardDataSource dataSource)
        {
            if (dataSource is RVSQLiteDataSource sqlite)
                sqlite.Database = ResolvePath(userContext, dataSource.Id);

            return Task.FromResult(dataSource);
        }

        /// <summary>
        /// Resolve the .sqlite file for a request. Order:
        ///  1. the datasource Id IS a known sourceId — the metadata-catalog convention
        ///     (catalog datasource Id == sourceId). This is what makes AI metadata
        ///     generation work per source: its background user context has NULL
        ///     Properties, but the catalog datasource id round-trips here.
        ///  2. legacy aliases from pre-multi-source dashboards: "NorthwindSql" (AI-saved)
        ///     and "sqlServer" (engine-generated) both meant the northwind DB.
        ///  3. the active source carried by the user context (X-DataSource header / share claim).
        ///  4. the default source.
        /// </summary>
        private string ResolvePath(IRVUserContext? userContext, string? dataSourceId)
        {
            var byId = _registry.Find(dataSourceId);
            if (byId is not null) return byId.SqlitePath;

            if (string.Equals(dataSourceId, "NorthwindSql", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataSourceId, "sqlServer", StringComparison.OrdinalIgnoreCase))
            {
                var northwind = _registry.Find(SourceRegistry.DefaultSourceId);
                if (northwind is not null) return northwind.SqlitePath;
            }

            var fromContext = userContext?.Properties is not null
                && userContext.Properties.TryGetValue("SourceId", out var sid)
                    ? sid?.ToString()
                    : null;

            return _registry.Resolve(fromContext).SqlitePath;
        }
    }
}
