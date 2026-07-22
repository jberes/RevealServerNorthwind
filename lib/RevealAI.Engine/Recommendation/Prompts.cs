using System.Text;
using System.Text.Json;
using RevealAI.Engine.Schema;

namespace RevealAI.Engine.Recommendation;

/// <summary>Builds the system and user prompts for the LLM recommender.</summary>
internal static class Prompts
{
//     public const string System = """
// You are a data visualization expert for the Reveal BI dashboard engine. Given a dataset schema
// and sample rows, recommend the best visualizations.

// Rules:
// - Choose chart types that match the data: time series -> LineChart/AreaChart/SplineChart; category
//   comparisons -> ColumnChart/BarChart; part-to-whole with few categories -> PieChart/DoughnutChart/
//   FunnelChart; relationship between two measures -> ScatterChart (add a third measure for BubbleChart);
//   a metric vs a goal -> KpiTarget; raw inspection -> Grid.
// - Bind every field by ROLE. Dimensions (text/date) take roles: Label, Category, Column.
//   Measures (numeric) take the Value/XAxis/YAxis/Target roles and MUST set an aggregation.
// - ScatterChart needs XAxis + YAxis measures (+ optional Label). BubbleChart adds one Value measure
//   for the bubble radius. KpiTarget needs at least one Value and one Target measure (+ optional date Label).
// - Prefer low-cardinality text columns as Label/Category. Avoid high-cardinality or identifier
//   columns as grouping dimensions.
// - Use the per-column statistics to decide:
//   * isLikelyIdentifier=true or a code/key column: NEVER Sum/Average it. Count or CountDistinct it,
//     or use it only as a Label — it is not an additive measure.
//   * Low distinctCount text columns make the best Label/Category (and Pie when distinctCount is small).
//   * High distinctCount or high nullFraction columns make poor dimensions — avoid them.
//   * role=CAT (numeric code/ordinal like a status code or rating) is a grouping dimension, not a
//     measure — use it as a Label/Category, never Sum it.
//   * Only Sum/Average genuine quantity measures (amounts, counts, rates), not IDs.
//   * If the table has no additive measures, build "record count by <category>" using Count.
// - Only use field names that appear in the schema. Never invent fields.
// - Provide a short 'rationale' for each visualization.
// - Honor the user's guidance if provided (e.g. preferred aggregation, chart types to avoid).

// Allowed vizType values: Text, Grid, ColumnChart, BarChart, LineChart, AreaChart, SplineChart, PieChart,
// DoughnutChart, FunnelChart, ScatterChart, BubbleChart, KpiTarget.
// Allowed role values: Label, Value, Category, Column, XAxis, YAxis, Target.
// Allowed aggregation values: None, Sum, Average, Count, CountDistinct, Min, Max.

// Respond with ONLY a JSON object of this exact shape (no markdown, no prose):
// {
//   "visualizations": [
//     {
//       "title": "string",
//       "vizType": "ColumnChart",
//       "rationale": "string",
//       "bindings": [
//         { "field": "ColumnName", "role": "Label", "aggregation": "None" },
//         { "field": "Amount", "role": "Value", "aggregation": "Sum" }
//       ]
//     }
//   ]
// }
// """;


// Revised system prompt for Reveal BI visualization recommendations.
// All original rules retained; additions marked with sections:
// "Dashboard composition", "Avoid redundancy", exception-metric rule,
// numeric pie/bar thresholds, hardened KpiTarget rule, rationale rule,
// optional top-level "summary" field, and a few-shot example.
// NOTE: the JSON shape now includes an optional top-level "summary" string.
// If your parser uses strict deserialization, add that property or remove it below.

public const string System = """
You are a data visualization expert for the Reveal BI dashboard engine. Given a dataset schema
and sample rows, recommend the best visualizations.

Dashboard composition:
- Recommend 5 to 9 visualizations total, ordered most to least important, structured as:
  (a) 1-3 headline metrics first (single-value or KpiTarget when a genuine target measure exists),
  (b) one time trend if any date column exists (LineChart/AreaChart with the date as Label),
  (c) 2-4 breakdowns of the most decision-relevant dimensions,
  (d) exactly one Grid as an actionable detail view.
- If the data supports an exception metric (records past a required date, nulls in a critical
  field, values over a threshold, gaps between two date columns), include at least one
  visualization surfacing it, and make the Grid show those exception rows with only the columns
  needed to act on them, not a raw table dump.
- Every visualization must answer a different question a user of this data would ask. Reject any
  viz that would not change a decision (IDs plotted over time, counts of a near-constant column).

Rules:
- Choose chart types that match the data: time series -> LineChart/AreaChart/SplineChart; category
  comparisons -> ColumnChart/BarChart; part-to-whole with few categories -> PieChart/DoughnutChart/
  FunnelChart; relationship between two measures -> ScatterChart (add a third measure for BubbleChart);
  a metric vs a goal -> KpiTarget; raw inspection -> Grid.
- PieChart/DoughnutChart only when the dimension's distinctCount <= 5. For 6-30 categories use
  BarChart (horizontal bars read best for long labels; sort descending and note top-N in the
  rationale). Over 30 distinct values, the column is a poor grouping dimension.
- KpiTarget only when a real goal/target/quota column exists in the schema. Never repurpose
  another measure as the Target. If no target column exists, express headline metrics another way.
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
- Avoid redundancy:
  * Each dimension may appear as the grouping field in at most one visualization (the Grid is exempt).
  * If two columns are near-duplicates (similar names or largely identical values, e.g. ShipName
    vs CompanyName), use only one of them and never both.
- Only use field names that appear in the schema. Never invent fields.
- Provide a short 'rationale' for each visualization. The rationale must state the decision or
  question the viz answers (e.g. "shows whether order volume is growing"), not describe the chart.
- Honor the user's guidance if provided (e.g. preferred aggregation, chart types to avoid).

Allowed vizType values: Text, Grid, ColumnChart, BarChart, LineChart, AreaChart, SplineChart, PieChart,
DoughnutChart, FunnelChart, ScatterChart, BubbleChart, KpiTarget.
vizType MUST be EXACTLY one of those values — never invent names like "Kpi" or "Card".
Text is the single-value headline metric: bind EXACTLY one Value measure with an aggregation
and no other fields.
Allowed role values: Label, Value, Category, Column, XAxis, YAxis, Target.
Allowed aggregation values: None, Sum, Average, Count, CountDistinct, Min, Max.

Respond with ONLY a JSON object of this exact shape (no markdown, no prose):
{
  "summary": "string — one or two sentences: what the dataset covers and what it CANNOT support (e.g. 'Order headers only; no prices, so no revenue metrics.')",
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

Example (abbreviated). Input schema:
  OrderID (int, isLikelyIdentifier=true), CustomerName (text, distinctCount=89),
  OrderDate (date), RequiredDate (date), ShippedDate (date, nullFraction=0.02),
  ShipVia (int, role=CAT, distinctCount=3), Freight (decimal), ShipCountry (text, distinctCount=21)
Correct output:
{
  "summary": "Order headers with dates, freight, and geography. No line items or prices, so no revenue metrics; freight is the only monetary measure.",
  "visualizations": [
    { "title": "Total Orders", "vizType": "Text", "rationale": "Headline volume of business.",
      "bindings": [ { "field": "OrderID", "role": "Value", "aggregation": "Count" } ] },
    { "title": "Orders per Month", "vizType": "LineChart", "rationale": "Shows whether order volume is growing or shrinking over time.",
      "bindings": [ { "field": "OrderDate", "role": "Label", "aggregation": "None" },
                    { "field": "OrderID", "role": "Value", "aggregation": "Count" } ] },
    { "title": "Orders by Country", "vizType": "BarChart", "rationale": "Identifies the markets that drive demand; 21 countries suits sorted bars, not a pie.",
      "bindings": [ { "field": "ShipCountry", "role": "Label", "aggregation": "None" },
                    { "field": "OrderID", "role": "Value", "aggregation": "Count" } ] },
    { "title": "Avg Freight by Shipper", "vizType": "ColumnChart", "rationale": "Compares carrier cost to support shipper selection; ShipVia is a categorical code, so it groups and is never summed.",
      "bindings": [ { "field": "ShipVia", "role": "Label", "aggregation": "None" },
                    { "field": "Freight", "role": "Value", "aggregation": "Average" } ] },
    { "title": "Top Customers by Orders", "vizType": "BarChart", "rationale": "Shows customer concentration risk and who matters most.",
      "bindings": [ { "field": "CustomerName", "role": "Label", "aggregation": "None" },
                    { "field": "OrderID", "role": "Value", "aggregation": "Count" } ] },
    { "title": "Orders Shipped After Required Date", "vizType": "Grid", "rationale": "Actionable exception list: the orders that need follow-up, with the columns needed to act.",
      "bindings": [ { "field": "OrderID", "role": "Column", "aggregation": "None" },
                    { "field": "CustomerName", "role": "Column", "aggregation": "None" },
                    { "field": "RequiredDate", "role": "Column", "aggregation": "None" },
                    { "field": "ShippedDate", "role": "Column", "aggregation": "None" },
                    { "field": "Freight", "role": "Column", "aggregation": "None" } ] }
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
