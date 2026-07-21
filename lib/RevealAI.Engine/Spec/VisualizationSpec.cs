namespace RevealAI.Engine.Spec;

/// <summary>
/// A provider-neutral description of a single visualization: WHAT to chart and HOW to bind
/// fields. The LLM produces these; the Compiler turns them into Reveal visualizations.
/// </summary>
public sealed class VisualizationSpec
{
    public string Title { get; set; } = string.Empty;

    public VizType VizType { get; set; } = VizType.Grid;

    public List<FieldBinding> Bindings { get; set; } = new();

    /// <summary>Short human-readable explanation of why this viz fits the data (from the LLM).</summary>
    public string? Rationale { get; set; }

    /// <summary>Dashboard grid width (Reveal uses a column-span model). Defaults set by the Compiler.</summary>
    public int ColumnSpan { get; set; }

    /// <summary>Dashboard grid height.</summary>
    public int RowSpan { get; set; }

    /// <summary>
    /// Confidence score 0..1 the recommender assigns. Used for ranking; not sent to Reveal.
    /// </summary>
    public double Score { get; set; }

    /// <summary>Convenience: all bindings for a given role.</summary>
    public IEnumerable<FieldBinding> ByRole(FieldRole role) => Bindings.Where(b => b.Role == role);
}
