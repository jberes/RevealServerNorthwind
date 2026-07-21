using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Recommendation;

/// <summary>Tuning knobs for a recommendation request.</summary>
public sealed class RecommendationRequest
{
    /// <summary>Maximum number of visualizations to return.</summary>
    public int MaxRecommendations { get; set; } = 6;

    /// <summary>
    /// Optional natural-language guidance from the user, e.g. "use Count of rows instead of Sum",
    /// "focus on revenue trends", "no pie charts". Passed to the LLM; ignored by the heuristic.
    /// </summary>
    public string? Guidance { get; set; }
}

/// <summary>Result of a recommendation run, including which engine produced it.</summary>
public sealed class RecommendationResult
{
    public List<VisualizationSpec> Visualizations { get; set; } = new();

    /// <summary>"llm" or "heuristic" — useful for diagnostics / UI badges.</summary>
    public string Source { get; set; } = "heuristic";

    /// <summary>Non-fatal notes (e.g. dropped invalid suggestions, LLM fallback reason).</summary>
    public List<string> Warnings { get; set; } = new();
}
