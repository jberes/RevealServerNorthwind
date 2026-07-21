namespace RevealAI.Engine.Spec;

/// <summary>
/// Binds one schema field to a role within a visualization, with an optional aggregation.
/// </summary>
public sealed class FieldBinding
{
    /// <summary>The column name, must exist in the dataset schema.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>How this field is used in the visualization.</summary>
    public FieldRole Role { get; set; } = FieldRole.Value;

    /// <summary>
    /// Aggregation for measure roles (Value/XAxis/YAxis/Target). Ignored for dimensions.
    /// Defaults to <see cref="AggregationKind.Sum"/> when a measure has no explicit aggregation.
    /// </summary>
    public AggregationKind Aggregation { get; set; } = AggregationKind.None;

    /// <summary>
    /// Time bucket for a date field used as a Label/Date axis. Set automatically by the validator
    /// for temporal labels (based on the date span) so charts aggregate by year/quarter/month/day.
    /// </summary>
    public DateGrain DateGrain { get; set; } = DateGrain.None;

    public FieldBinding() { }

    public FieldBinding(string field, FieldRole role, AggregationKind aggregation = AggregationKind.None)
    {
        Field = field;
        Role = role;
        Aggregation = aggregation;
    }
}
