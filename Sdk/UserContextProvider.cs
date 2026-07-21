using Microsoft.Extensions.Options;
using Reveal.Sdk;

namespace RevealSdk.Sdk
{
    public class UserContextProvider : IRVUserContextProvider
    {
        private readonly SqlServerOptions _sqlOptions;

        public UserContextProvider(IOptions<SqlServerOptions> sqlOptions)
        {
            _sqlOptions = sqlOptions.Value;
        }

        IRVUserContext IRVUserContextProvider.GetUserContext(HttpContext aspnetContext)
        {
            var props = new Dictionary<string, object?>
            {
                ["Host"] = _sqlOptions.Host,
                ["Database"] = _sqlOptions.Database,
                ["Username"] = _sqlOptions.Username,
                ["Password"] = _sqlOptions.Password,
                ["Schema"] = _sqlOptions.Schema,
                ["TrustServerCertificate"] = _sqlOptions.TrustServerCertificate,
                ["FilterTables"] = Array.Empty<string>()
            };

            return new RVUserContext(null, props);
        }
    }
}
