using Reveal.Sdk;
using Reveal.Sdk.Data;

namespace RevealSdk.Sdk
{
    /// <summary>
    /// Intentionally permissive: the Connections page browses ALL tables/views, and the
    /// AI-only restriction lives in the metadata catalog (AiCatalogService, Restricted
    /// discovery) — not in a runtime object filter. Kept registered as the hook point
    /// for future per-user restrictions.
    /// </summary>
    public class DataSourceItemFilter : IRVObjectFilter
    {
        public Task<bool> Filter(IRVUserContext userContext, RVDashboardDataSource dataSource)
            => Task.FromResult(true);

        public Task<bool> Filter(IRVUserContext userContext, RVDataSourceItem dataSourceItem)
            => Task.FromResult(true);
    }
}
