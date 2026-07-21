using System.Text;
using System.Text.Json;
using RevealAI.Engine.Schema;

namespace RevealAI.Engine.Recommendation;

/// <summary>Builds the system and user prompts for the LLM recommender.</summary>
internal static class Prompts
{
    public const string System = """
You are a data visualization expert for the Reveal BI dashboard engine. Given a dataset schema
and sample rows, recommend the best visualizations.

Rules:
- Choose chart types that match the data: time series -> LineChart/AreaChart/SplineChart; category
  comparisons -> ColumnChart/BarChart; part-to-whole with few categories -> PieChart/DoughnutChart/
  FunnelChart; relationship between two measures -> ScatterChart (add a third measure for BubbleChart);
  a metric vs a goal -> KpiTarget; raw inspection -> Grid.
- Bind every field by ROLE. Dimensions (text/date) take roles: Label, Category, Column.
  Measures (numeric) take the Value/XAxis/YAxis/Target roles and MUST set an aggregation.
- ScatterChart needs XAxis + YAxis measures (+ optional Label). BubbleChart adds one Value measure
  for the bubble radius. KpiTarget needs at least one Value and one Target measure (+ optional date Label).
- Prefer low-cardinality text columns as Label/Category. Avoid high-cardinality or identifier
  columns as grouping dimensions.
- Use the per-column statistics to decide:
  * isLikelyIdentifier=true or a code/key column: NEVER Sum/Average it. Count or CountDistinct it,
    or use it only as a Label — it is not an additive measure.
  * Low distinctCount text columns make the best Label/Category (and Pie when distinctCount is small).
  * High distinctCount or high nullFraction columns make poor dimensions — avoid them.
  * role=CAT (numeric code/ordinal like a status code or rating) is a grouping dimension, not a
    measure — use it as a Label/Category, never Sum it.
  * Only Sum/Average genuine quantity measures (amounts, counts, rates), not IDs.
  * If the table has no additive measures, build "record count by <category>" using Count.
- Only use field names that appear in the schema. Never invent fields.
- Provide a short 'rationale' for each visualization.
- Honor the user's guidance if provided (e.g. preferred aggregation, chart types to avoid).

Allowed vizType values: Grid, ColumnChart, BarChart, LineChart, AreaChart, SplineChart, PieChart,
DoughnutChart, FunnelChart, ScatterChart, BubbleChart, KpiTarget.
Allowed role values: Label, Value, Category, Column, XAxis, YAxis, Target.
Allowed aggregation values: None, Sum, Average, Count, CountDistinct, Min, Max.

Respond with ONLY a JSON object of this exact shape (no markdown, no prose):
{
  "visualizations": [
    {
      "title": "string",
      "vizType": "ColumnChart",
      "rationale": "string",
      "bindings": [
        { "field": "ColumnName", "role": "Label", "aggregation": "None" },
        { "field": "Amount", "role": "Value", "aggregation": "Sum" }
      ]
    }
  ]
}
""";

    public static string BuildUser(DatasetSchema schema, RecommendationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Dataset: {schema.Name}");
        sb.AppendLine($"Return at most {request.MaxRecommendations} visualizations, best first.");
        if (!string.IsNullOrWhiteSpace(request.Guidance))
            sb.AppendLine($"User guidance: {request.Guidance}");
        if (schema.RowCount is long rc)
            sb.AppendLine($"Row count: {rc}{(schema.StatsAreEstimates ? " (estimated from sample)" : "")}");
        sb.AppendLine();
        sb.AppendLine("Columns (name | dataType | semanticTag | distinct | null% | role | min..max | samples):");
        sb.AppendLine("  role: ID = identifier, CAT = numeric category/code, MEA = measure, DIM = dimension.");
        foreach (var c in schema.Columns)
        {
            var samples = c.SampleValues.Count > 0 ? string.Join(", ", c.SampleValues.Take(3)) : "-";
            var nullPct = c.NullFraction is double nf ? $"{nf * 100:0}%" : "?";
            var range = (c.Min is not null || c.Max is not null) ? $"{c.Min}..{c.Max}" : "-";
            var role = c.IsLikelyIdentifier ? "ID"
                : c.IsLikelyCategorical ? "CAT"
                : c.IsMeasure ? "MEA"
                : c.IsDimension ? "DIM" : "-";
            sb.AppendLine($"- {c.Name} | {c.DataType} | {c.SemanticTag} | {c.DistinctCount?.ToString() ?? "?"} | {nullPct} | {role} | {range} | {samples}");
        }

        if (schema.SampleRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Sample rows (JSON):");
            var rows = schema.SampleRows.Take(5);
            sb.AppendLine(JsonSerializer.Serialize(rows));
        }

        return sb.ToString();
    }
}
