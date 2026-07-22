using Reveal.Sdk;
using RevealSdk.Services;

namespace RevealSdk.Sdk
{
    /// <summary>
    /// Carries the ACTIVE DATA SOURCE with every Reveal request so the providers can
    /// resolve per-source paths. Resolution order:
    ///   1. X-DataSource header (set globally by the client fetch wrapper + Reveal headers)
    ///   2. "sourceId" JWT claim (share tokens embed the shared dashboard's source)
    ///   3. the default source ("northwind")
    /// The old FilteredTables whitelist is gone: /connections browses everything and the
    /// AI-only restriction lives in the metadata catalog (AiCatalogService).
    /// </summary>
    public class UserContextProvider : IRVUserContextProvider
    {
        private readonly SourceRegistry _registry;

        public UserContextProvider(SourceRegistry registry)
        {
            _registry = registry;
        }

        IRVUserContext IRVUserContextProvider.GetUserContext(HttpContext aspnetContext)
        {
            // aspnetContext is NULL when the Reveal engine resolves a context outside an
            // HTTP request — e.g. AI metadata generation triggered from a background task.
            // Those flows carry the source via the datasource Id instead (see
            // DataSourceProvider.ResolvePath rule 1), so the default here is harmless.
            var requested = aspnetContext?.Request?.Headers["X-DataSource"].FirstOrDefault()
                            ?? aspnetContext?.User?.FindFirst("sourceId")?.Value;

            // Validate against the registry so a bogus header can't point at arbitrary paths.
            var sourceId = _registry.Find(requested)?.SourceId ?? SourceRegistry.DefaultSourceId;

            var props = new Dictionary<string, object?>
            {
                ["SourceId"] = sourceId
            };

            return new RVUserContext(aspnetContext?.User?.Identity?.Name, props);
        }
    }
}
