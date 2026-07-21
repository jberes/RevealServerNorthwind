using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Microsoft.SqlServer;

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
            if (dataSourceItem is not RVSqlServerDataSourceItem sqlDataSourceItem)
            {
                return Task.FromResult(true);
            }

            if (!userContext.Properties.TryGetValue("FilterTables", out var filterTablesObj) ||
                filterTablesObj is not string[] filterTables ||
                filterTables.Length == 0)
            {
                return Task.FromResult(true);
            }

            var tableBlocked = sqlDataSourceItem.Table != null && !filterTables.Contains(sqlDataSourceItem.Table);
            var procedureBlocked = sqlDataSourceItem.Procedure != null && !filterTables.Contains(sqlDataSourceItem.Procedure);

            return Task.FromResult(!tableBlocked && !procedureBlocked);
        }
    }
}
