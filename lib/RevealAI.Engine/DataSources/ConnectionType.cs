using System.Text.Json.Serialization;

namespace RevealAI.Engine.DataSources;

/// <summary>
/// The kind of data source connection. Maps to a Reveal connector in <c>DataSourceResolver</c>.
/// Add entries here and a corresponding case in the resolver to support more connectors.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionType
{
    SqlServer,
    AzureSqlServer,
    MySql,
    PostgreSql,
    Oracle,
    AmazonRedshift,
    Snowflake,
    GoogleBigQuery,
    /// <summary>SQLite database file (local, no credentials). Database = file path.</summary>
    Sqlite,
    /// <summary>Excel workbook reached over a public/anonymous REST URL.</summary>
    Excel,
    /// <summary>CSV file reached over a public/anonymous REST URL.</summary>
    Csv,
    /// <summary>Generic JSON REST endpoint.</summary>
    Rest
}
