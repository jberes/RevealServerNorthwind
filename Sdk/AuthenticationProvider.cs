using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Microsoft.SqlServer;
using RevealSdk.Services;

namespace RevealSdk.Sdk
{
    /// <summary>
    /// Supplies credentials for config-defined SQL Server sources (username/password
    /// from the source's appsettings section). SQLite sources are local files and
    /// need none. Credentials never leave the server — the client only ever sees
    /// the source id.
    /// </summary>
    public class AuthenticationProvider : IRVAuthenticationProvider
    {
        private readonly SourceRegistry _registry;

        public AuthenticationProvider(SourceRegistry registry)
        {
            _registry = registry;
        }

        public Task<IRVDataSourceCredential> ResolveCredentialsAsync(
            IRVUserContext userContext, RVDashboardDataSource dataSource)
        {
            IRVDataSourceCredential credential = new RVUsernamePasswordDataSourceCredential();

            if (dataSource is RVSqlServerDataSource sqlServer)
            {
                // Match by datasource id first (catalog convention: id == sourceId),
                // then by host+database (editor-created datasources with random ids).
                var src = _registry.Find(sqlServer.Id)
                          ?? _registry.GetSources().FirstOrDefault(s =>
                              s.SqlServer is not null
                              && string.Equals(s.SqlServer.Host, sqlServer.Host, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(s.SqlServer.Database, sqlServer.Database, StringComparison.OrdinalIgnoreCase))
                          ?? _registry.GetSources().FirstOrDefault(s => s.SqlServer is not null);

                if (src?.SqlServer is not null)
                    credential = new RVUsernamePasswordDataSourceCredential(
                        src.SqlServer.Username, src.SqlServer.Password);
            }

            return Task.FromResult(credential);
        }
    }
}
