using Reveal.Sdk;
using RevealSdk.Services;

namespace RevealSdk.Sdk
{
    /// <summary>
    /// Loads/saves dashboards from the per-source folder layout:
    /// Dashboards/{sourceId}/{dashboardId}.rdash. The active source comes from the
    /// user context (X-DataSource header / share-token claim via UserContextProvider).
    /// GET falls back to searching every source folder so legacy links and the share
    /// viewer resolve even when the caller's active source differs.
    /// </summary>
    public sealed class DashboardProvider : IRVDashboardProvider
    {
        private readonly SourceRegistry _registry;

        public DashboardProvider(SourceRegistry registry)
        {
            _registry = registry;
        }

        public Task<Dashboard> GetDashboardAsync(IRVUserContext userContext, string dashboardId)
        {
            var path = Path.Combine(ResolveDashboardsDir(userContext), dashboardId + ".rdash");

            if (!File.Exists(path))
            {
                path = _registry.GetSources()
                           .Select(s => Path.Combine(s.DashboardsDir, dashboardId + ".rdash"))
                           .FirstOrDefault(File.Exists)
                       ?? path; // let the Dashboard ctor surface the not-found error
            }

            return Task.FromResult(new Dashboard(path));
        }

        public Task SaveDashboardAsync(IRVUserContext userContext, string dashboardId, Dashboard dashboard)
        {
            var dir = ResolveDashboardsDir(userContext);
            Directory.CreateDirectory(dir);
            return dashboard.SaveToFileAsync(Path.Combine(dir, dashboardId + ".rdash"));
        }

        private string ResolveDashboardsDir(IRVUserContext? userContext)
        {
            var sourceId = userContext?.Properties is not null
                && userContext.Properties.TryGetValue("SourceId", out var sid)
                    ? sid?.ToString()
                    : null;
            return _registry.Resolve(sourceId).DashboardsDir;
        }
    }
}
