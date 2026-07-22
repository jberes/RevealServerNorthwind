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
            var parsed = ParseVisualizations(raw, result.Warnings);
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

    private static List<VisualizationSpec> ParseVisualizations(string raw, List<string> warnings)
    {
        var json = ExtractJsonObject(raw);
        using var doc = JsonDocument.Parse(json);

        // Expected shape is {"visualizations":[...]}, but some models return the
        // bare array at the root despite the prompt — accept both.
        JsonElement arr;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            arr = doc.RootElement;
        else if (!doc.RootElement.TryGetProperty("visualizations", out arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<VisualizationSpec>();

        // Deserialize element-wise so ONE bad entry (e.g. an invented vizType that
        // isn't in the enum) drops that entry with a warning instead of discarding
        // the whole batch and falling back to heuristics. Common invented type
        // names are mapped onto real ones before giving up.
        var specs = new List<VisualizationSpec>();
        foreach (var element in arr.EnumerateArray())
        {
            try
            {
                var spec = JsonSerializer.Deserialize<VisualizationSpec>(element.GetRawText(), JsonOptions)
                           ?? TryRepairSpec(element);
                if (spec is not null) specs.Add(spec);
            }
            catch (JsonException ex)
            {
                var repaired = TryRepairSpec(element);
                if (repaired is not null)
                {
                    specs.Add(repaired);
                    continue;
                }
                var title = element.ValueKind == JsonValueKind.Object
                            && element.TryGetProperty("title", out var t) ? t.GetString() : null;
                warnings.Add($"Skipped one recommendation{(title is null ? "" : $" (\"{title}\")")}: {ex.Message}");
            }
        }
        return specs;
    }

    /// <summary>Models occasionally invent viz type names ("Kpi", "Table", "Donut"…) — map them
    /// onto the closest real VizType and retry the deserialization.</summary>
    private static readonly Dictionary<string, string> VizTypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Single-value metric names map to the Text view (targetless "KPIs").
        ["kpi"] = "Text", ["kpitime"] = "Text", ["kpicard"] = "Text",
        ["card"] = "Text", ["singlevalue"] = "Text", ["metric"] = "Text",
        ["textview"] = "Text",
        ["table"] = "Grid", ["datagrid"] = "Grid",
        ["column"] = "ColumnChart", ["bar"] = "BarChart", ["line"] = "LineChart",
        ["area"] = "AreaChart", ["pie"] = "PieChart", ["donut"] = "DoughnutChart",
        ["doughnut"] = "DoughnutChart", ["funnel"] = "FunnelChart",
        ["scatter"] = "ScatterChart", ["bubble"] = "BubbleChart",
    };

    private static VisualizationSpec? TryRepairSpec(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("vizType", out var vt)
            || vt.ValueKind != JsonValueKind.String
            || !VizTypeAliases.TryGetValue(vt.GetString() ?? "", out var mapped))
            return null;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!;
            node["vizType"] = mapped;
            return JsonSerializer.Deserialize<VisualizationSpec>(node.ToJsonString(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

        // Isolate the outermost JSON value — object OR array (some models emit a
        // bare array at the root).
        int objStart = text.IndexOf('{');
        int arrStart = text.IndexOf('[');
        if (arrStart >= 0 && (objStart < 0 || arrStart < objStart))
        {
            int arrEnd = text.LastIndexOf(']');
            if (arrEnd > arrStart) return text[arrStart..(arrEnd + 1)];
        }
        int objEnd = text.LastIndexOf('}');
        return objStart >= 0 && objEnd > objStart ? text[objStart..(objEnd + 1)] : text;
    }
}
