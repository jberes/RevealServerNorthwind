namespace RevealAI.Engine.Schema;

/// <summary>
/// Name-based lexicons used to classify numeric columns deterministically: additive quantities
/// (Sum), ratios/rates (Average), and codes/ordinals (categorical dimension, not a measure).
/// High-precision tokens only; the LLM (when configured) handles the ambiguous cases.
/// </summary>
public static class ColumnSemantics
{
    // Ratios / rates / per-unit values / scores -> Average, not Sum.
    // (A unit "price" is per-row; summing prices across rows is meaningless.)
    private static readonly string[] RatioTokens =
    {
        "rate", "ratio", "pct", "percent", "percentage", "utilization", "utilisation",
        "coverage", "ltv", "score", "index", "avg", "average", "mean", "margin", "price",
    };

    // Additive quantities -> Sum. Also protects these from being mistaken for ordinal codes.
    private static readonly string[] AdditiveTokens =
    {
        "amount", "amt", "total", "balance", "qty", "quantity", "count", "volume", "revenue",
        "sales", "spend", "cost", "fee", "units", "sold", "gross", "net", "principal",
        "exposure", "commitment", "outstanding", "advance", "payment", "deposit", "withdrawal",
    };

    // Codes / ordinals / categories -> categorical dimension, not a measure.
    private static readonly string[] CodeTokens =
    {
        "code", "type", "status", "level", "rating", "grade", "tier", "class", "category", "categ",
        "group", "flag", "segment", "band", "bucket", "kind", "mode", "stage", "phase", "priority",
        "severity", "year", "quarter", "month",
    };

    public static bool IsRatioName(string name) => ContainsAny(name, RatioTokens);
    public static bool IsAdditiveName(string name) => ContainsAny(name, AdditiveTokens);
    public static bool IsCodeName(string name) => ContainsAny(name, CodeTokens);

    private static bool ContainsAny(string name, string[] tokens)
    {
        var n = name.ToLowerInvariant();
        return tokens.Any(t => n.Contains(t));
    }
}
