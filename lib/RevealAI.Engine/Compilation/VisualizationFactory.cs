using Reveal.Sdk.Dom; // DateAggregationType
using Reveal.Sdk.Dom.Data;
using Reveal.Sdk.Dom.Visualizations;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Compilation;

/// <summary>
/// Builds a concrete Reveal visualization from a <see cref="VisualizationSpec"/> using the fluent
/// binding API. The set of supported types is intentionally explicit; unsupported types throw
/// <see cref="NotSupportedException"/> so the Compiler can skip them with a clear warning.
/// </summary>
public sealed class VisualizationFactory
{
    public static readonly IReadOnlySet<VizType> SupportedTypes = new HashSet<VizType>
    {
        VizType.Grid, VizType.ColumnChart, VizType.BarChart, VizType.LineChart,
        VizType.AreaChart, VizType.SplineChart, VizType.PieChart, VizType.DoughnutChart,
        VizType.FunnelChart, VizType.ScatterChart, VizType.BubbleChart, VizType.KpiTarget,
        VizType.Text
    };

    public IVisualization Build(VisualizationSpec spec, DataSourceItem dataSourceItem)
    {
        var title = string.IsNullOrWhiteSpace(spec.Title) ? spec.VizType.ToString() : spec.Title;

        IVisualization viz = spec.VizType switch
        {
            VizType.Grid => Grid(spec, dataSourceItem, title),
            VizType.ColumnChart => Category(new ColumnChartVisualization(title, dataSourceItem), spec),
            VizType.BarChart => Category(new BarChartVisualization(title, dataSourceItem), spec),
            VizType.LineChart => Category(new LineChartVisualization(title, dataSourceItem), spec),
            VizType.AreaChart => Category(new AreaChartVisualization(title, dataSourceItem), spec),
            VizType.SplineChart => Category(new SplineChartVisualization(title, dataSourceItem), spec),
            VizType.PieChart => SingleValue(new PieChartVisualization(title, dataSourceItem), spec),
            VizType.DoughnutChart => SingleValue(new DoughnutChartVisualization(title, dataSourceItem), spec),
            VizType.FunnelChart => SingleValue(new FunnelChartVisualization(title, dataSourceItem), spec),
            VizType.ScatterChart => Axis(new ScatterVisualization(title, dataSourceItem), spec),
            VizType.BubbleChart => Bubble(new BubbleVisualization(title, dataSourceItem), spec),
            VizType.KpiTarget => Kpi(new KpiTargetVisualization(title, dataSourceItem), spec),
            VizType.Text => Text(new TextVisualization(title, dataSourceItem), spec),
            _ => throw new NotSupportedException(
                $"VizType '{spec.VizType}' is not supported by the Compiler. Supported: {string.Join(", ", SupportedTypes)}.")
        };

        ApplyLayout(viz, spec);
        return viz;
    }

    private const int GridPageSize = 100;

    private static GridVisualization Grid(VisualizationSpec spec, DataSourceItem dsi, string title)
    {
        var columns = spec.Bindings
            .Where(b => b.Role is FieldRole.Column or FieldRole.Label or FieldRole.Value)
            .Select(b => b.Field)
            .ToArray();
        var grid = new GridVisualization(title, dsi).SetColumns(columns);
        grid.ConfigureSettings(s =>
        {
            s.IsPagingEnabled = true;   // requested: paging on every grid
            s.PageSize = GridPageSize;  // 100 rows per page
        });
        return grid;
    }

    /// <summary>Column/Bar/Line/Area/Spline: label + measures + optional category.</summary>
    private static T Category<T>(T viz, VisualizationSpec spec)
        where T : Visualization, ILabels, IValues, ICategory
    {
        SetLabelBinding(viz, spec.ByRole(FieldRole.Label).FirstOrDefault());

        var values = spec.ByRole(FieldRole.Value).Select(ToNumberField).ToArray();
        if (values.Length > 0) viz.SetValues(values);

        var category = spec.ByRole(FieldRole.Category).FirstOrDefault();
        if (category is not null) viz.SetCategory(category.Field);

        return viz;
    }

    /// <summary>Pie/Doughnut/Funnel: single label + single value.</summary>
    private static T SingleValue<T>(T viz, VisualizationSpec spec)
        where T : Visualization, ILabels, IValues
    {
        SetLabelBinding(viz, spec.ByRole(FieldRole.Label).FirstOrDefault());

        var value = spec.ByRole(FieldRole.Value).FirstOrDefault();
        if (value is not null) viz.SetValue(ToNumberField(value));

        return viz;
    }

    /// <summary>Scatter: label + X measure + Y measure.</summary>
    private static T Axis<T>(T viz, VisualizationSpec spec)
        where T : Visualization, ILabels, IAxis
    {
        SetLabelBinding(viz, spec.ByRole(FieldRole.Label).FirstOrDefault());

        var x = spec.ByRole(FieldRole.XAxis).FirstOrDefault();
        if (x is not null) viz.SetXAxis(ToNumberField(x));

        var y = spec.ByRole(FieldRole.YAxis).FirstOrDefault();
        if (y is not null) viz.SetYAxis(ToNumberField(y));

        return viz;
    }

    /// <summary>Bubble: scatter axes + a Value-role measure mapped to bubble radius.</summary>
    private static BubbleVisualization Bubble(BubbleVisualization viz, VisualizationSpec spec)
    {
        Axis(viz, spec);
        var radius = spec.ByRole(FieldRole.Value).FirstOrDefault();
        if (radius is not null) viz.SetRadius(ToNumberField(radius));
        return viz;
    }

    /// <summary>KPI Target: optional Date dimension + Value measures vs Target measures.</summary>
    private static KpiTargetVisualization Kpi(KpiTargetVisualization viz, VisualizationSpec spec)
    {
        var date = spec.ByRole(FieldRole.Label).FirstOrDefault();
        if (date is not null)
        {
            if (date.DateGrain != DateGrain.None) viz.SetDate(new DateDataField(date.Field) { AggregationType = MapGrain(date.DateGrain) });
            else viz.SetDate(date.Field);
        }

        var values = spec.ByRole(FieldRole.Value).Select(ToNumberField).ToArray();
        if (values.Length > 0) viz.SetValues(values);

        var targets = spec.ByRole(FieldRole.Target).Select(ToNumberField).ToArray();
        if (targets.Length > 0) viz.SetTargets(targets);

        return viz;
    }

    /// <summary>Text (single-value headline metric): one aggregated Value measure.</summary>
    private static TextVisualization Text(TextVisualization viz, VisualizationSpec spec)
    {
        var values = spec.ByRole(FieldRole.Value).Select(ToNumberField).ToArray();
        if (values.Length > 0) viz.SetValues(values);
        return viz;
    }

    /// <summary>Bind a Label, using a bucketed date field when a grain is set (e.g. by month/year).</summary>
    private static void SetLabelBinding<T>(T viz, FieldBinding? label) where T : ILabels
    {
        if (label is null) return;
        if (label.DateGrain != DateGrain.None)
            viz.SetLabel(new DateDataField(label.Field) { AggregationType = MapGrain(label.DateGrain) });
        else
            viz.SetLabel(label.Field);
    }

    private static NumberDataField ToNumberField(FieldBinding binding)
    {
        var field = new NumberDataField(binding.Field) { AggregationType = MapAggregation(binding.Aggregation) };
        field.Formatting.LargeNumberFormat = LargeNumberFormat.Auto; // requested: Large Number Formatting = Auto
        return field;
    }

    private static AggregationType MapAggregation(AggregationKind kind) => kind switch
    {
        AggregationKind.Sum => AggregationType.Sum,
        AggregationKind.Average => AggregationType.Avg,
        AggregationKind.Count => AggregationType.CountRows,
        AggregationKind.CountDistinct => AggregationType.CountDistinct,
        AggregationKind.Min => AggregationType.Min,
        AggregationKind.Max => AggregationType.Max,
        _ => AggregationType.Sum
    };

    private static DateAggregationType MapGrain(DateGrain grain) => grain switch
    {
        DateGrain.Year => DateAggregationType.Year,
        DateGrain.Quarter => DateAggregationType.Quarter,
        DateGrain.Month => DateAggregationType.Month,
        DateGrain.Day => DateAggregationType.Day,
        _ => DateAggregationType.Month
    };

    private static void ApplyLayout(IVisualization viz, VisualizationSpec spec)
    {
        viz.ColumnSpan = spec.ColumnSpan > 0 ? spec.ColumnSpan : (spec.VizType == VizType.Grid ? 60 : 30);
        viz.RowSpan = spec.RowSpan > 0 ? spec.RowSpan : 20;
    }
}
