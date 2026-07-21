using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.SQLite;

namespace RevealSdk.Sdk
{
    public class DataSourceItemFilter : IRVObjectFilter
    {
        public Task<bool> Filter(IRVUserContext userContext, RVDashboardDataSource dataSource)
        {
            return Task.FromResult(true);
        }

        public Task<bool> Filter(IRVUserContext userContext, RVDataSourceItem dataSourceItem)
        {
            if (dataSourceItem is not RVSQLiteDataSourceItem sqliteItem)
            {
                return Task.FromResult(true);
            }

            // The whitelist travels with the user context (populated by UserContextProvider
            // from the metadata catalog's Tables collection). Absent/empty => no restriction.
            // Properties can be null for non-HTTP contexts (e.g. AI metadata generation).
            if (userContext?.Properties is null ||
                !userContext.Properties.TryGetValue("FilteredTables", out var filteredTablesObj) ||
                filteredTablesObj is not string[] filteredTables ||
                filteredTables.Length == 0)
            {
                return Task.FromResult(true);
            }

            var allowed = new HashSet<string>(filteredTables, StringComparer.OrdinalIgnoreCase);

            // Allow ONLY items whose table/view is in the whitelist. Items with no table
            // (e.g. a raw custom query) are left untouched.
            var tableBlocked = sqliteItem.Table != null && !allowed.Contains(sqliteItem.Table);

            return Task.FromResult(!tableBlocked);
        }
    }
}
