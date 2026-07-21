using RevealAI.Engine.Schema;

namespace RevealAI.Engine.Recommendation;

/// <summary>
/// Produces ranked visualization recommendations for a dataset schema. Implementations may use an
/// LLM or pure heuristics; both return validated <c>VisualizationSpec</c>s.
/// </summary>
public interface IVisualizationRecommender
{
    Task<RecommendationResult> RecommendAsync(
        DatasetSchema schema,
        RecommendationRequest? request = null,
        CancellationToken ct = default);
}
