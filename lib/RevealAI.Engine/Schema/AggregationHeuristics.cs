using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Schema;

/// <summary>
/// Deterministic default aggregation for a measure, chosen from the column's type, semantic tag,
/// value range, and name. High-precision rules only — the LLM (when configured) can still override.
/// </summary>
public static class AggregationHeuristics
{
    public static AggregationKind Suggest(ColumnSchema col)
    {
        // Non-numeric, identifier, or categorical-code columns can only be counted.
        if (col.DataType != DataType.Number || col.IsLikelyIdentifier || col.IsLikelyCategorical)
            return AggregationKind.Count;

        // Ratios / rates / per-unit values are averaged, even if currency-typed (e.g. unit "price").
        if (col.SemanticTag == SemanticTag.Percentage || LooksLikeRatio(col))
            return AggregationKind.Average;

        // Additive quantities and (non-per-unit) currency amounts are summed.
        return AggregationKind.Sum;
    }

    private static bool LooksLikeRatio(ColumnSchema col)
    {
        if (ColumnSemantics.IsRatioName(col.Name))
            return true;

        // A non-integer numeric bounded to [0, 1] is almost certainly a fraction/ratio.
        if (!col.IsInteger
            && double.TryParse(col.Min, out var min) && double.TryParse(col.Max, out var max)
            && min >= 0 && max <= 1 && max > 0)
            return true;

        return false;
    }
}
