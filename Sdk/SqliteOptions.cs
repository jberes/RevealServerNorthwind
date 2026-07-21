namespace RevealSdk.Sdk
{
    /// <summary>
    /// Configuration for the SQLite data source, bound from the "Sqlite" section of
    /// appsettings.json. SQLite is a local file — there is no host, database name, or
    /// credentials (the Azure SQL Server connection was removed).
    /// </summary>
    public class SqliteOptions
    {
        /// <summary>
        /// Path to the SQLite database file. Relative paths are resolved against the
        /// application content root (see Program.cs). Defaults to the shipped Northwind DB.
        /// </summary>
        public string DatabasePath { get; set; } = "Data/northwind.sqlite";

        /// <summary>
        /// Whitelist of table/view names (bare) that make up the Reveal metadata catalog.
        /// This single list is the source of truth used two ways:
        ///   1) it builds the "Restricted" catalog.json handed to the Reveal AI SDK, and
        ///   2) it filters the tables/views the Connections page (/sql/objects) shows.
        /// Empty means "no whitelist" (fall back to showing everything).
        /// </summary>
        public string[] CatalogObjects { get; set; } = Array.Empty<string>();
    }
}
