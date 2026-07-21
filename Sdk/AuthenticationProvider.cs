using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Microsoft.SqlServer;

namespace RevealSdk.Sdk
{
    public class AuthenticationProvider : IRVAuthenticationProvider
    {
        public Task<IRVDataSourceCredential> ResolveCredentialsAsync(IRVUserContext userContext, RVDashboardDataSource dataSource)
        {
            IRVDataSourceCredential credential = new RVIntegratedAuthenticationCredential();

            if (dataSource is RVSqlServerDataSource)
            {
                var username = userContext.Properties.TryGetValue("Username", out var usernameObj)
                    ? usernameObj?.ToString()
                    : null;
                var password = userContext.Properties.TryGetValue("Password", out var passwordObj)
                    ? passwordObj?.ToString()
                    : null;

                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    credential = new RVUsernamePasswordDataSourceCredential(username, password);
                }
            }

            return Task.FromResult(credential);
        }
    }
}
