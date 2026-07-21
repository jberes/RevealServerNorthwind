using Microsoft.Extensions.Options;
using Reveal.Sdk;

namespace RevealSdk.Sdk
{
    public class UserContextProvider : IRVUserContextProvider
    {
        private readonly SqliteOptions _sqliteOptions;

        public UserContextProvider(IOptions<SqliteOptions> sqliteOptions)
        {
            _sqliteOptions = sqliteOptions.Value;
        }

        IRVUserContext IRVUserContextProvider.GetUserContext(HttpContext aspnetContext)
        {
            // The table whitelist that travels with the user context so DataSourceItemFilter
            // can enforce it. This is the SAME set that backs Reveal/Metadata/catalog.json —
            // both are built from Sqlite:CatalogObjects. SQLite has no schema, so table
            // names are bare; we still carry a "dbo."-qualified alias in case an AI/catalog
            // reference is schema-qualified. Empty when no whitelist is configured, which the
            // filter treats as "allow everything".
            var filteredTables = (_sqliteOptions.CatalogObjects ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .SelectMany(n => new[] { n, $"dbo.{n}" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var props = new Dictionary<string, object?>
            {
                ["FilteredTables"] = filteredTables
            };

            return new RVUserContext(null, props);
        }
    }
}
