using Microsoft.Extensions.Logging;
using Reveal.Sdk.Dom;
using RevealAI.Engine.DataSources;
using RevealAI.Engine.Schema;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Compilation;

public sealed class CompileResult
{
    public required RdashDocument Document { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Deterministically compiles a <see cref="DashboardSpec"/> + dataset schema into an
/// <c>RdashDocument</c>. No LLM involvement — given the same spec this always produces the same
/// dashboard. Unsupported or invalid visualizations are skipped with a warning rather than failing
/// the whole compile.
/// </summary>
public sealed class SpecCompiler
{
    private readonly ConnectionRegistry _connections;
    private readonly DataSourceResolver _resolver;
    private readonly VisualizationFactory _factory;
    private readonly ILogger<SpecCompiler> _logger;

    public SpecCompiler(
        ConnectionRegistry connections,
        DataSourceResolver resolver,
        VisualizationFactory factory,
        ILogger<SpecCompiler> logger)
    {
        _connections = connections;
        _resolver = resolver;
        _factory = factory;
        _logger = logger;
    }

    public CompileResult Compile(DashboardSpec spec, DatasetSchema schema)
    {
        // Inline connection (multi-tenant) takes precedence over a configured id.
        var connection = spec.Connection ?? _connections.Get(spec.ConnectionId);
        if (string.IsNullOrWhiteSpace(connection.Id))
            connection.Id = string.IsNullOrWhiteSpace(spec.ConnectionId) ? Guid.NewGuid().ToString("N") : spec.ConnectionId;
        var dataSourceItem = _resolver.Resolve(connection, schema, spec.Dataset);

        var document = new RdashDocument(string.IsNullOrWhiteSpace(spec.Title) ? "AI Generated Dashboard" : spec.Title)
        {
            Description = spec.Description ?? string.Empty
        };

        var result = new CompileResult { Document = document };

        foreach (var vizSpec in spec.Visualizations)
        {
            var error = SpecValidator.Validate(vizSpec, schema);
            if (error is not null)
            {
                result.Warnings.Add($"Skipped '{vizSpec.Title}': {error}");
                continue;
            }

            if (!VisualizationFactory.SupportedTypes.Contains(vizSpec.VizType))
            {
                result.Warnings.Add($"Skipped '{vizSpec.Title}': vizType {vizSpec.VizType} not yet supported by the Compiler.");
                continue;
            }

            try
            {
                var viz = _factory.Build(vizSpec, dataSourceItem);
                document.Visualizations.Add(viz);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build visualization '{Title}'.", vizSpec.Title);
                result.Warnings.Add($"Skipped '{vizSpec.Title}': {ex.Message}");
            }
        }

        document.Validate();
        return result;
    }
}
