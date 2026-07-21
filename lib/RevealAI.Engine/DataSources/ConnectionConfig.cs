namespace RevealAI.Engine.DataSources;

/// <summary>
/// A configured data source connection, typically bound from appsettings ("Connections" section).
/// Credentials are kept here (username/password) for the Reveal server's data-source provider to
/// resolve at serve time — they are NOT written into the generated .rdash file.
/// </summary>
public sealed class ConnectionConfig
{
    /// <summary>Stable id referenced by <c>DashboardSpec.ConnectionId</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display title shown in the Reveal data source list.</summary>
    public string Title { get; set; } = string.Empty;

    public ConnectionType Type { get; set; } = ConnectionType.Rest;

    // --- Database connectors (SqlServer, Redshift, Postgres, MySql, Oracle, Snowflake, ...) ---
    // For SQLite, Host/Schema/credentials are unused and Database holds the .sqlite file path.
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Database { get; set; }
    public string? Schema { get; set; }

    // --- File / REST connectors (Excel, Csv, Rest) ---
    /// <summary>Base URL for the file/endpoint (Excel/Csv/Rest connectors).</summary>
    public string? Url { get; set; }
    /// <summary>True if the URL needs no authentication.</summary>
    public bool IsAnonymous { get; set; } = true;

    // --- Credentials (username/password only, per the simple-auth requirement) ---
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>True when this connector targets a tabular database (table-based dataset).</summary>
    public bool IsDatabase => Type is ConnectionType.SqlServer or ConnectionType.AzureSqlServer
        or ConnectionType.MySql or ConnectionType.PostgreSql or ConnectionType.Oracle
        or ConnectionType.AmazonRedshift or ConnectionType.Snowflake or ConnectionType.GoogleBigQuery
        or ConnectionType.Sqlite;
}
