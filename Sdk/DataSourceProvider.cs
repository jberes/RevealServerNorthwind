using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Microsoft.SqlServer;
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
            else if (dataSourceItem is RVSqlServerDataSourceItem sqlItem)
            {
                ChangeDataSourceAsync(userContext, sqlItem.DataSource);

                var src = ResolveSqlServerSource(userContext, sqlItem.DataSource?.Id);
                if (src?.SqlServer is not null)
                {
                    if (string.IsNullOrWhiteSpace(sqlItem.Database)) sqlItem.Database = src.SqlServer.Database;
                    if (string.IsNullOrWhiteSpace(sqlItem.Schema)) sqlItem.Schema = src.SqlServer.Schema;
                }
                if (string.IsNullOrWhiteSpace(sqlItem.Table) && !string.IsNullOrWhiteSpace(sqlItem.Id))
                    sqlItem.Table = sqlItem.Id;
            }

            return Task.FromResult(dataSourceItem);
        }

        public Task<RVDashboardDataSource> ChangeDataSourceAsync(IRVUserContext userContext,
            RVDashboardDataSource dataSource)
        {
            if (dataSource is RVSQLiteDataSource sqlite)
            {
                sqlite.Database = ResolveSqlitePath(userContext, dataSource.Id);
            }
            else if (dataSource is RVSqlServerDataSource sqlServer)
            {
                var src = ResolveSqlServerSource(userContext, dataSource.Id);
                if (src?.SqlServer is not null)
                {
                    sqlServer.Host = src.SqlServer.Host;
                    sqlServer.Database = src.SqlServer.Database;
                    if (string.IsNullOrWhiteSpace(sqlServer.Schema)) sqlServer.Schema = src.SqlServer.Schema;
                    sqlServer.TrustServerCertificate = src.SqlServer.TrustServerCertificate;
                    sqlServer.Encrypt = true;
                }
            }

            return Task.FromResult(dataSource);
        }

        /// <summary>
        /// Resolve the .sqlite file for a request. Order:
        ///  1. the datasource Id IS a known SQLITE sourceId — the metadata-catalog
        ///     convention (catalog datasource Id == sourceId). This is what makes AI
        ///     metadata generation work per source: its background user context has
        ///     NULL Properties, but the catalog datasource id round-trips here.
        ///  2. legacy aliases from pre-multi-source dashboards: "NorthwindSql" (AI-saved)
        ///     and "sqlServer" (engine-generated) both meant the northwind DB.
        ///  3. the active source carried by the user context (X-DataSource header / share claim).
        ///  4. the default source.
        /// </summary>
        private string ResolveSqlitePath(IRVUserContext? userContext, string? dataSourceId)
        {
            var byId = _registry.Find(dataSourceId);
            if (byId?.SqlitePath is not null) return byId.SqlitePath;

            if (string.Equals(dataSourceId, "NorthwindSql", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataSourceId, "sqlServer", StringComparison.OrdinalIgnoreCase))
            {
                var northwind = _registry.Find(SourceRegistry.DefaultSourceId);
                if (northwind?.SqlitePath is not null) return northwind.SqlitePath;
            }

            var fromContext = ContextSourceId(userContext);
            var resolved = _registry.Resolve(fromContext);
            // A SQLite datasource must never resolve onto a SQL Server source.
            if (resolved.SqlitePath is null)
                resolved = _registry.GetSources().FirstOrDefault(s => s.SqlitePath is not null)
                           ?? throw new InvalidOperationException("No SQLite sources exist.");
            return resolved.SqlitePath!;
        }

        /// <summary>Same resolution order for SQL Server datasources (kind-checked).</summary>
        private SourceInfo? ResolveSqlServerSource(IRVUserContext? userContext, string? dataSourceId)
        {
            var byId = _registry.Find(dataSourceId);
            if (byId?.SqlServer is not null) return byId;

            var fromContext = _registry.Find(ContextSourceId(userContext));
            if (fromContext?.SqlServer is not null) return fromContext;

            return _registry.GetSources().FirstOrDefault(s => s.SqlServer is not null);
        }

        private static string? ContextSourceId(IRVUserContext? userContext) =>
            userContext?.Properties is not null
            && userContext.Properties.TryGetValue("SourceId", out var sid)
                ? sid?.ToString()
                : null;
    }
}
