using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Schema;

/// <summary>
/// Describes one column of a dataset: its name, type, and profiling statistics used by the recommender.
/// </summary>
public sealed class ColumnSchema
{
    public string Name { get; set; } = string.Empty;

    public DataType DataType { get; set; } = DataType.Text;

    public SemanticTag SemanticTag { get; set; } = SemanticTag.None;

    public bool Nullable { get; set; } = true;

    // ---- Profiling statistics (exact for SQL, sample-based for uploads) ----

    /// <summary>Number of distinct non-null values. Null if not profiled.</summary>
    public int? DistinctCount { get; set; }

    /// <summary>Count of non-null values observed.</summary>
    public long? NonNullCount { get; set; }

    /// <summary>Fraction of rows that are null (0..1). Null if not profiled.</summary>
    public double? NullFraction { get; set; }

    /// <summary>Min value (as string) for numeric/temporal columns.</summary>
    public string? Min { get; set; }

    /// <summary>Max value (as string) for numeric/temporal columns.</summary>
    public string? Max { get; set; }

    /// <summary>True if the numeric column holds whole numbers only (no fractional part).</summary>
    public bool IsInteger { get; set; }

    /// <summary>
    /// True when the column looks like an identifier/code (by name, or an integer whose distinct
    /// count ≈ row count). Identifiers must NOT be summed/averaged — count them instead.
    /// </summary>
    public bool IsLikelyIdentifier { get; set; }

    /// <summary>
    /// True when an integer column is really a categorical code/ordinal (e.g. status code, rating,
    /// tier) — used as a grouping dimension, not summed as a measure.
    /// </summary>
    public bool IsLikelyCategorical { get; set; }

    /// <summary>A few example values, for inference and LLM context.</summary>
    public List<string> SampleValues { get; set; } = new();

    /// <summary>True for numeric columns that make good additive measures (not IDs, codes, lat/long).</summary>
    public bool IsMeasure => DataType == DataType.Number
                             && !IsLikelyIdentifier
                             && !IsLikelyCategorical
                             && SemanticTag is not (SemanticTag.Identifier or SemanticTag.Latitude or SemanticTag.Longitude);

    /// <summary>True for columns that make grouping dimensions (text/bool, identifiers, or numeric codes).</summary>
    public bool IsDimension => DataType is DataType.Text or DataType.Boolean
                               || IsLikelyIdentifier || IsLikelyCategorical;

    public bool IsTemporal => DataType is DataType.Date or DataType.DateTime;
}
