using System.Text.Json;
using Microsoft.Extensions.Logging;
using RevealAI.Engine.Llm;
using RevealAI.Engine.Schema;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Recommendation;

/// <summary>
/// LLM-backed recommender. Asks the configured model for a list of visualizations, validates them
/// against the schema, and falls back to the heuristic recommender if the model is unavailable or
/// returns nothing usable.
/// </summary>
public sealed class LlmRecommender : IVisualizationRecommender
{
    private readonly ILlmClient _llm;
    private readonly ILogger<LlmRecommender> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LlmRecommender(ILlmClient llm, ILogger<LlmRecommender> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public async Task<RecommendationResult> RecommendAsync(
        DatasetSchema schema, RecommendationRequest? request = null, CancellationToken ct = default)
    {
        request ??= new RecommendationRequest();
        var result = new RecommendationResult { Source = "llm" };

        try
        {
            var raw = await _llm.CompleteAsync(Prompts.System, Prompts.BuildUser(schema, request), ct);
            var parsed = ParseVisualizations(raw);
            var valid = SpecValidator.KeepValid(parsed, schema, result.Warnings);

            if (valid.Count > 0)
            {
                // The LLM returns "best first"; preserve order with a descending synthetic score.
                for (int i = 0; i < valid.Count; i++)
                    valid[i].Score = 1.0 - i * 0.01;
                result.Visualizations = valid.Take(request.MaxRecommendations).ToList();
                return result;
            }

            result.Warnings.Add("LLM returned no valid visualizations; using heuristic recommendations.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM recommendation failed ({Provider}); falling back to heuristics.", _llm.Provider);
            result.Warnings.Add($"LLM call failed ({ex.Message}); using heuristic recommendations.");
        }

        // Fallback.
        var fallback = await new HeuristicRecommender().RecommendAsync(schema, request, ct);
        result.Source = "heuristic-fallback";
        result.Visualizations = fallback.Visualizations;
        result.Warnings.AddRange(fallback.Warnings);
        return result;
    }

    private static List<VisualizationSpec> ParseVisualizations(string raw)
    {
        var json = ExtractJsonObject(raw);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("visualizations", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<VisualizationSpec>();

        return JsonSerializer.Deserialize<List<VisualizationSpec>>(arr.GetRawText(), JsonOptions)
               ?? new List<VisualizationSpec>();
    }

    /// <summary>Strip markdown fences / stray prose and isolate the outermost JSON object.</summary>
    private static string ExtractJsonObject(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
        }

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
