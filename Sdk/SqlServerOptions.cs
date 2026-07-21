namespace RevealSdk.Sdk
{
    public class SqlServerOptions
    {
        public string? Host { get; set; }
        public string? Database { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Schema { get; set; }
        public bool TrustServerCertificate { get; set; } = true;

        /// <summary>
        /// Whitelist of table/view names (bare, unqualified) that make up the Reveal
        /// metadata catalog. This single list is the source of truth used two ways:
        ///   1) it builds the "Restricted" catalog.json handed to the Reveal AI SDK, and
        ///   2) it filters the tables/views the Connections page (/sql/objects) shows.
        /// Empty means "no whitelist" (fall back to showing everything).
        /// </summary>
        public string[] CatalogObjects { get; set; } = Array.Empty<string>();
    }
}
