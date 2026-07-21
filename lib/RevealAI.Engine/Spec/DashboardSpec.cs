using RevealAI.Engine.DataSources;

namespace RevealAI.Engine.Spec;

/// <summary>
/// A full dashboard description: which connection + dataset to use, and the visualizations to build.
/// This is the input to <c>SpecCompiler</c> that produces an <c>RdashDocument</c>.
/// </summary>
public sealed class DashboardSpec
{
    public string Title { get; set; } = "AI Generated Dashboard";

    public string? Description { get; set; }

    /// <summary>
    /// Id of a configured connection (see <c>ConnectionConfig.Id</c>). Determines which Reveal
    /// data-source connector is built (SQL Server, Redshift, Excel, REST, ...).
    /// Ignored when <see cref="Connection"/> is supplied.
    /// </summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Optional inline connection (multi-tenant): supply the connection details on the request
    /// instead of referencing a pre-configured id. Used as-is and never persisted.
    /// </summary>
    public ConnectionConfig? Connection { get; set; }

    /// <summary>
    /// Identifies the dataset within the connection: a table name (SQL), a sheet name (Excel),
    /// a URL/path, etc. Interpretation depends on the connection type.
    /// </summary>
    public string Dataset { get; set; } = string.Empty;

    public List<VisualizationSpec> Visualizations { get; set; } = new();
}
