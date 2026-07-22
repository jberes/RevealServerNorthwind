using System.Text.Json.Serialization;

namespace RevealAI.Engine.Spec;

/// <summary>
/// The data type of a column, as inferred from a schema or sample rows.
/// Maps to a concrete Reveal field type (TextField / NumberField / DateField / DateTimeField).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataType
{
    Text,
    Number,
    Date,
    DateTime,
    Boolean
}

/// <summary>
/// Optional semantic hint layered on top of <see cref="DataType"/>. Helps the recommender
/// pick map / KPI / currency-aware visualizations. Inferred heuristically or supplied by the caller.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SemanticTag
{
    None,
    Identifier,
    Currency,
    Percentage,
    Latitude,
    Longitude,
    Geography,
    HighCardinality
}

/// <summary>
/// The visualization types the engine can recommend. The Compiler implements a subset (see
/// <c>VisualizationFactory.SupportedTypes</c>); unsupported types produce a clean error rather
/// than an invalid dashboard.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VizType
{
    Grid,
    ColumnChart,
    BarChart,
    LineChart,
    AreaChart,
    SplineChart,
    PieChart,
    DoughnutChart,
    FunnelChart,
    ScatterChart,
    BubbleChart,
    KpiTarget,
    Pivot,
    /// <summary>Single-value headline metric (Reveal's Text view): one aggregated measure.</summary>
    Text
}

/// <summary>
/// The role a field plays in a visualization. Maps to the Reveal fluent binding methods
/// (SetLabel / SetValues / SetCategory / SetColumns / etc.).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldRole
{
    /// <summary>Category/X-axis dimension (SetLabel).</summary>
    Label,

    /// <summary>Numeric measure (SetValue/SetValues), aggregated.</summary>
    Value,

    /// <summary>Series breakdown dimension (SetCategory).</summary>
    Category,

    /// <summary>Plain tabular column (Grid / Pivot value column).</summary>
    Column,

    /// <summary>Pivot row dimension.</summary>
    Row,

    /// <summary>Scatter / bubble X measure.</summary>
    XAxis,

    /// <summary>Scatter / bubble Y measure.</summary>
    YAxis,

    /// <summary>KPI target measure.</summary>
    Target
}

/// <summary>
/// Aggregation applied to a numeric measure. Maps to Reveal's <c>AggregationType</c>.
/// This is the primary knob the user tweaks ("use Count instead of Sum").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AggregationKind
{
    None,
    Sum,
    Average,
    Count,
    CountDistinct,
    Min,
    Max
}

/// <summary>
/// Time bucket for a date dimension on an axis. Without this, a raw date column with thousands of
/// distinct values produces an unusable chart. Maps to Reveal's <c>DateAggregationType</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DateGrain
{
    None,
    Year,
    Quarter,
    Month,
    Day
}
