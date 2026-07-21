using RevealAI.Engine.Schema;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Recommendation;

/// <summary>
/// Rule-based recommender. Produces sensible default visualizations with no LLM call. Used directly
/// when no LLM is configured, and as the fallback when an LLM call fails or returns nothing usable.
/// </summary>
public sealed class HeuristicRecommender : IVisualizationRecommender
{
    private const int LowCardinalityThreshold = 50;
    private const int PieSliceThreshold = 8;

    public Task<RecommendationResult> RecommendAsync(
        DatasetSchema schema, RecommendationRequest? request = null, CancellationToken ct = default)
    {
        request ??= new RecommendationRequest();
        var result = new RecommendationResult { Source = "heuristic" };
        var specs = Build(schema);

        result.Visualizations = SpecValidator.KeepValid(specs, schema, result.Warnings)
            .OrderByDescending(s => s.Score)
            .Take(request.MaxRecommendations)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>Public so the LLM recommender can reuse it as a guaranteed fallback.</summary>
    public static List<VisualizationSpec> Build(DatasetSchema schema)
    {
        var specs = new List<VisualizationSpec>();
        var measures = schema.Measures.ToList();
        var temporals = schema.Temporals.ToList();
        var goodDimensions = schema.Dimensions
            .Where(d => d.SemanticTag != SemanticTag.HighCardinality
                        // needs real variation: at least 2 distinct values, not mostly null
                        && (d.DistinctCount is null || (d.DistinctCount >= 2 && d.DistinctCount <= LowCardinalityThreshold))
                        && (d.NullFraction is null || d.NullFraction < 0.5))
            // prefer genuine categories over identifier-like keys, then lower cardinality first
            .OrderBy(d => d.IsLikelyIdentifier ? 1 : 0)
            .ThenBy(d => d.DistinctCount ?? int.MaxValue)
            .ToList();

        // 1) Time series: temporal + measures -> line chart. Only combine measures that share the
        //    same aggregation (don't mix a summed amount and an averaged price on one axis).
        if (temporals.Count > 0 && measures.Count > 0)
        {
            var time = temporals[0];
            var primaryAgg = DefaultAggregation(measures[0]);
            var seriesMeasures = measures.Where(m => DefaultAggregation(m) == primaryAgg).Take(3).ToList();

            var spec = new VisualizationSpec
            {
                Title = $"{measures[0].Name} over {time.Name}",
                VizType = VizType.LineChart,
                Score = 0.95,
                Rationale = "A temporal column paired with numeric measures suggests a trend over time."
            };
            spec.Bindings.Add(new FieldBinding(time.Name, FieldRole.Label));
            foreach (var m in seriesMeasures)
                spec.Bindings.Add(new FieldBinding(m.Name, FieldRole.Value, DefaultAggregation(m)));
            specs.Add(spec);
        }

        // 2) Category breakdowns: each low-cardinality dimension vs the primary measure
        //    (or a record count, when the table has no additive numeric measures).
        var primary = measures.FirstOrDefault();
        foreach (var dim in goodDimensions.Take(3))
        {
            var measureBinding = primary is not null
                ? new FieldBinding(primary.Name, FieldRole.Value, DefaultAggregation(primary))
                : new FieldBinding(dim.Name, FieldRole.Value, AggregationKind.Count);
            var metric = primary?.Name ?? "record count";

            var spec = new VisualizationSpec
            {
                Title = $"{Capitalize(metric)} by {dim.Name}",
                VizType = VizType.ColumnChart,
                Score = 0.85,
                Rationale = $"'{dim.Name}' is a low-cardinality category ({dim.DistinctCount?.ToString() ?? "?"} values); comparing {metric} across it is a natural column comparison."
            };
            spec.Bindings.Add(new FieldBinding(dim.Name, FieldRole.Label));
            spec.Bindings.Add(Clone(measureBinding));
            specs.Add(spec);

            // A pie is part-to-whole, so only when the measure is additive (Sum) and 3..8 slices.
            // (Averaging or counting a fraction of a "whole" is meaningless.)
            if (measureBinding.Aggregation == AggregationKind.Sum && dim.DistinctCount is >= 3 and <= PieSliceThreshold)
            {
                var pie = new VisualizationSpec
                {
                    Title = $"Share by {dim.Name}",
                    VizType = VizType.PieChart,
                    Score = 0.7,
                    Rationale = $"Few distinct values of '{dim.Name}' make a part-to-whole pie chart readable."
                };
                pie.Bindings.Add(new FieldBinding(dim.Name, FieldRole.Label));
                pie.Bindings.Add(Clone(measureBinding));
                specs.Add(pie);
            }
        }

        // 3) Two measures -> scatter (relationship), plotting one point per entity. A scatter needs
        //    an entity label (e.g. the asset name/id); without it there is "no data to display".
        var entityLabel = schema.Dimensions
            .OrderBy(d => d.IsLikelyIdentifier ? 1 : 0)        // prefer a readable name over a raw id
            .ThenByDescending(d => d.DistinctCount ?? 0)        // but a high-cardinality entity column
            .FirstOrDefault();
        if (measures.Count >= 2 && entityLabel is not null)
        {
            var scatter = new VisualizationSpec
            {
                Title = $"{measures[1].Name} vs {measures[0].Name}",
                VizType = measures.Count >= 3 ? VizType.BubbleChart : VizType.ScatterChart,
                Score = 0.6,
                Rationale = $"Plots {measures[0].Name} against {measures[1].Name}, one point per {entityLabel.Name}."
            };
            scatter.Bindings.Add(new FieldBinding(entityLabel.Name, FieldRole.Label));
            scatter.Bindings.Add(new FieldBinding(measures[0].Name, FieldRole.XAxis, DefaultAggregation(measures[0])));
            scatter.Bindings.Add(new FieldBinding(measures[1].Name, FieldRole.YAxis, DefaultAggregation(measures[1])));
            if (measures.Count >= 3)
                scatter.Bindings.Add(new FieldBinding(measures[2].Name, FieldRole.Value, DefaultAggregation(measures[2])));
            specs.Add(scatter);
        }

        // 4) An "actual vs target" currency pair -> KPI Target.
        var currencyMeasures = measures.Where(m => m.SemanticTag == SemanticTag.Currency).ToList();
        if (currencyMeasures.Count >= 2)
        {
            var kpi = new VisualizationSpec
            {
                Title = $"{currencyMeasures[0].Name} vs {currencyMeasures[1].Name}",
                VizType = VizType.KpiTarget,
                Score = 0.65,
                Rationale = "Two currency measures look like an actual-vs-target comparison, well suited to a KPI."
            };
            if (temporals.Count > 0) kpi.Bindings.Add(new FieldBinding(temporals[0].Name, FieldRole.Label));
            kpi.Bindings.Add(new FieldBinding(currencyMeasures[0].Name, FieldRole.Value, AggregationKind.Sum));
            kpi.Bindings.Add(new FieldBinding(currencyMeasures[1].Name, FieldRole.Target, AggregationKind.Sum));
            specs.Add(kpi);
        }

        // 5) Always offer a data grid of the raw columns (capped for readability).
        var grid = new VisualizationSpec
        {
            Title = $"{schema.Name} details",
            VizType = VizType.Grid,
            Score = 0.5,
            Rationale = "A raw data grid is always useful for inspecting the underlying records."
        };
        foreach (var col in schema.Columns.Take(12))
            grid.Bindings.Add(new FieldBinding(col.Name, FieldRole.Column,
                col.IsMeasure ? DefaultAggregation(col) : AggregationKind.None));
        specs.Add(grid);

        return specs;
    }

    private static AggregationKind DefaultAggregation(ColumnSchema column) =>
        AggregationHeuristics.Suggest(column);

    private static FieldBinding Clone(FieldBinding b) => new(b.Field, b.Role, b.Aggregation);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
