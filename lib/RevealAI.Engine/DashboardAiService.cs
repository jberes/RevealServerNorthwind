using RevealAI.Engine.Compilation;
using RevealAI.Engine.Recommendation;
using RevealAI.Engine.Schema;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine;

/// <summary>
/// The single entry point used by hosts (Web API, MCP server). Wraps schema building,
/// recommendation, and compilation behind a small surface.
/// </summary>
public sealed class DashboardAiService
{
    private readonly IVisualizationRecommender _recommender;
    private readonly SpecCompiler _compiler;

    public DashboardAiService(IVisualizationRecommender recommender, SpecCompiler compiler)
    {
        _recommender = recommender;
        _compiler = compiler;
    }

    /// <summary>
    /// Build a dataset schema from an explicit column list and/or sample rows. If columns are
    /// omitted, types are inferred from the rows. If both are present, sample values enrich the
    /// columns and any Text-typed column is re-inferred.
    /// </summary>
    public DatasetSchema BuildSchema(
        string datasetName,
        IReadOnlyList<ColumnSchema>? columns,
        IReadOnlyList<Dictionary<string, string?>>? sampleRows)
    {
        if ((columns is null || columns.Count == 0) && sampleRows is { Count: > 0 })
            return SchemaInference.FromSampleRows(datasetName, sampleRows);

        var schema = new DatasetSchema
        {
            Name = datasetName,
            Columns = columns?.ToList() ?? new List<ColumnSchema>(),
            SampleRows = sampleRows?.Take(50).ToList() ?? new List<Dictionary<string, string?>>()
        };

        // Attach sample values to columns, re-infer any still Text-by-default, and profile stats.
        if (schema.SampleRows.Count > 0)
        {
            foreach (var col in schema.Columns.Where(c => c.SampleValues.Count == 0))
            {
                col.SampleValues = schema.SampleRows
                    .Select(r => r.TryGetValue(col.Name, out var v) ? v : null)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .Take(5)
                    .ToList();
            }
            SchemaInference.Enrich(schema);
            DataProfiler.ProfileFromSampleRows(schema);
        }

        return schema;
    }

    public Task<RecommendationResult> RecommendAsync(
        DatasetSchema schema, RecommendationRequest? options = null, CancellationToken ct = default)
        => _recommender.RecommendAsync(schema, options, ct);

    public CompileResult Compile(DashboardSpec spec, DatasetSchema schema)
        => _compiler.Compile(spec, schema);

    /// <summary>
    /// One-shot "DashboardBuilder": recommend visualizations for the schema, then compile the top
    /// picks into a dashboard — no manual spec editing in between. The recommender source and any
    /// recommendation warnings are merged into the compile result's warnings.
    /// </summary>
    public async Task<CompileResult> BuildAsync(
        string title,
        string connectionId,
        string dataset,
        DatasetSchema schema,
        RecommendationRequest? options = null,
        CancellationToken ct = default,
        DataSources.ConnectionConfig? connection = null)
    {
        var recommendations = await _recommender.RecommendAsync(schema, options, ct);

        var spec = new DashboardSpec
        {
            Title = title,
            ConnectionId = connectionId,
            Connection = connection,
            Dataset = dataset,
            Visualizations = recommendations.Visualizations
        };

        var result = _compiler.Compile(spec, schema);
        result.Warnings.Insert(0, $"Recommendations from: {recommendations.Source}.");
        result.Warnings.AddRange(recommendations.Warnings);
        return result;
    }
}
