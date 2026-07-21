using RevealAI.Engine.Schema;

namespace RevealAI.Engine.Spec;

/// <summary>
/// Validates and repairs <see cref="VisualizationSpec"/>s against a dataset schema. Shared by the
/// recommenders (to drop bad LLM suggestions) and the Compiler (to fail fast with clear errors).
/// </summary>
public static class SpecValidator
{
    /// <summary>
    /// Validate one spec. Returns null if valid; otherwise a human-readable reason. Performs light
    /// in-place repair (default aggregation on measures, fixing field-name casing to the schema).
    /// </summary>
    public static string? Validate(VisualizationSpec spec, DatasetSchema schema)
    {
        if (spec.Bindings.Count == 0)
            return $"'{spec.Title}' ({spec.VizType}) has no field bindings.";

        foreach (var binding in spec.Bindings)
        {
            var col = schema.Column(binding.Field);
            if (col is null)
                return $"'{spec.Title}' references unknown field '{binding.Field}'.";

            // Normalize field name to the exact schema casing.
            binding.Field = col.Name;

            var isMeasureRole = binding.Role is FieldRole.Value or FieldRole.XAxis or FieldRole.YAxis or FieldRole.Target;
            if (isMeasureRole)
            {
                // Default aggregation by column signature (currency->Sum, ratio->Average, id->Count).
                if (binding.Aggregation == AggregationKind.None)
                    binding.Aggregation = AggregationHeuristics.Suggest(col);

                // Sum/Average/Min/Max require a numeric column; Count/CountDistinct work on any type.
                var additive = binding.Aggregation is AggregationKind.Sum or AggregationKind.Average
                    or AggregationKind.Min or AggregationKind.Max;
                if (additive && col.DataType != DataType.Number)
                    return $"'{spec.Title}': field '{col.Name}' is {col.DataType} but uses additive aggregation {binding.Aggregation}.";
            }
            else
            {
                // Dimensions don't carry aggregation.
                binding.Aggregation = AggregationKind.None;

                // A date used as an axis label must be bucketed, or the chart has thousands of points.
                if (binding.Role == FieldRole.Label && col.IsTemporal && binding.DateGrain == DateGrain.None)
                    binding.DateGrain = ChooseDateGrain(col);
            }
        }

        // Per-viz-type minimal-binding rules.
        return spec.VizType switch
        {
            VizType.Grid or VizType.Pivot => null, // any columns are fine
            VizType.PieChart or VizType.DoughnutChart or VizType.FunnelChart =>
                Require(spec, FieldRole.Label, 1) ?? Require(spec, FieldRole.Value, 1),
            VizType.ScatterChart or VizType.BubbleChart =>
                Require(spec, FieldRole.XAxis, 1) ?? Require(spec, FieldRole.YAxis, 1),
            VizType.KpiTarget =>
                Require(spec, FieldRole.Value, 1) ?? Require(spec, FieldRole.Target, 1),
            _ => // category charts: column/bar/line/area/spline
                Require(spec, FieldRole.Label, 1) ?? Require(spec, FieldRole.Value, 1)
        };
    }

    /// <summary>Pick a sensible date bucket from the column's date span (min..max).</summary>
    private static DateGrain ChooseDateGrain(ColumnSchema col)
    {
        if (DateTime.TryParse(col.Min, out var min) && DateTime.TryParse(col.Max, out var max) && max > min)
        {
            var days = (max - min).TotalDays;
            if (days > 365 * 3) return DateGrain.Year;     // multi-year span
            if (days > 120) return DateGrain.Month;        // several months to a few years
            return DateGrain.Day;                          // short span
        }
        return DateGrain.Month; // sensible default when the span is unknown
    }

    private static string? Require(VisualizationSpec spec, FieldRole role, int min)
    {
        var count = spec.Bindings.Count(b => b.Role == role);
        return count >= min ? null : $"'{spec.Title}' ({spec.VizType}) requires at least {min} {role} binding(s).";
    }

    /// <summary>Filter a list to only valid specs, collecting reasons for the dropped ones.</summary>
    public static List<VisualizationSpec> KeepValid(
        IEnumerable<VisualizationSpec> specs, DatasetSchema schema, List<string> warnings)
    {
        var kept = new List<VisualizationSpec>();
        foreach (var spec in specs)
        {
            var error = Validate(spec, schema);
            if (error is null) kept.Add(spec);
            else warnings.Add($"Dropped suggestion: {error}");
        }
        return kept;
    }
}
