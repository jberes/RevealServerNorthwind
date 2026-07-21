using System.Globalization;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Schema;

/// <summary>
/// Infers column data types and semantic tags from sample rows. Used when the caller supplies
/// only "top N rows" without an explicit schema, or to enrich a partial schema.
/// </summary>
public static class SchemaInference
{
    /// <summary>
    /// Build a <see cref="DatasetSchema"/> from sample rows alone, inferring types per column.
    /// </summary>
    public static DatasetSchema FromSampleRows(string name, IReadOnlyList<Dictionary<string, string?>> rows)
    {
        var schema = new DatasetSchema { Name = name };
        if (rows.Count == 0) return schema;

        var columnNames = rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var col in columnNames)
        {
            var values = rows
                .Select(r => r.TryGetValue(col, out var v) ? v : null)
                .ToList();
            schema.Columns.Add(InferColumn(col, values));
        }

        schema.SampleRows = rows.Take(50).ToList();
        DataProfiler.ProfileFromSampleRows(schema);
        return schema;
    }

    /// <summary>
    /// Re-infer types/tags for Text-typed columns from their sample values. Use for sources WITHOUT
    /// authoritative types (uploads / caller-supplied name-only columns). Not for DB introspection.
    /// </summary>
    public static void Enrich(DatasetSchema schema)
    {
        foreach (var col in schema.Columns)
        {
            if (col.SampleValues.Count == 0 || col.DataType != DataType.Text)
                continue;
            var inferred = InferColumn(col.Name, col.SampleValues!);
            col.DataType = inferred.DataType;
            if (col.SemanticTag == SemanticTag.None)
                col.SemanticTag = inferred.SemanticTag;
        }
        TagSemantics(schema);
    }

    /// <summary>
    /// Like <see cref="Enrich"/> but only re-infers the named columns (from sample values).
    /// Used by SQLite introspection to type columns that have NO declared type (computed view
    /// columns) WITHOUT re-typing declared text columns whose values merely look numeric
    /// (e.g. PostalCode "98122", Extension "5467") — which would make Reveal read them as
    /// integers and throw "cast String to Int64" at render. Does not call TagSemantics
    /// (the caller runs it once afterward).
    /// </summary>
    internal static void EnrichOnly(DatasetSchema schema, ISet<string> columnNames)
    {
        foreach (var col in schema.Columns)
        {
            if (!columnNames.Contains(col.Name) || col.SampleValues.Count == 0 || col.DataType != DataType.Text)
                continue;
            var inferred = InferColumn(col.Name, col.SampleValues!);
            col.DataType = inferred.DataType;
            if (col.SemanticTag == SemanticTag.None)
                col.SemanticTag = inferred.SemanticTag;
        }
    }

    /// <summary>
    /// Set semantic tags (Currency, Percentage, Geography, Identifier, HighCardinality, lat/long)
    /// for every column WITHOUT changing data types. Safe for DB introspection where types are
    /// authoritative. Uses exact distinct counts when available.
    /// </summary>
    public static void TagSemantics(DatasetSchema schema)
    {
        foreach (var col in schema.Columns)
        {
            if (col.SemanticTag != SemanticTag.None)
                continue;
            var distinct = col.DistinctCount ?? col.SampleValues.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            col.SemanticTag = InferSemanticTag(col.Name, col, col.SampleValues, distinct);
        }
    }

    private static ColumnSchema InferColumn(string name, IReadOnlyList<string?> rawValues)
    {
        var values = rawValues.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        var nullable = rawValues.Any(string.IsNullOrWhiteSpace);
        var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var column = new ColumnSchema
        {
            Name = name,
            Nullable = nullable,
            DistinctCount = distinct == 0 ? null : distinct,
            SampleValues = values.Take(5).ToList(),
            DataType = DataType.Text
        };

        if (values.Count == 0)
            return column;

        if (values.All(IsBoolean))
            column.DataType = DataType.Boolean;
        else if (values.All(IsNumber))
            column.DataType = DataType.Number;
        else if (values.All(IsDateTimeWithTime))
            column.DataType = DataType.DateTime;
        else if (values.All(IsDate))
            column.DataType = DataType.Date;
        else
            column.DataType = DataType.Text;

        column.SemanticTag = InferSemanticTag(name, column, values, distinct);
        return column;
    }

    private static SemanticTag InferSemanticTag(string name, ColumnSchema column, List<string> values, int distinct)
    {
        var lower = name.ToLowerInvariant();

        if (column.DataType == DataType.Number)
        {
            if (lower == "lat" || lower.Contains("latitude"))
                return SemanticTag.Latitude;
            if (lower is "lon" or "lng" || lower.Contains("longitude"))
                return SemanticTag.Longitude;
            if (lower == "id" || lower.EndsWith("id") || lower.Contains("_id"))
                return SemanticTag.Identifier;
            if (lower.Contains("price") || lower.Contains("cost") || lower.Contains("amount")
                || lower.Contains("revenue") || lower.Contains("sales") || lower.Contains("spend")
                || lower.Contains("budget"))
                return SemanticTag.Currency;
            if (lower.Contains("pct") || lower.Contains("percent") || lower.Contains("rate")
                || values.All(v => v.EndsWith("%")))
                return SemanticTag.Percentage;
        }
        else if (column.DataType == DataType.Text)
        {
            if (lower.Contains("country") || lower.Contains("state") || lower.Contains("region")
                || lower.Contains("city") || lower.Contains("province"))
                return SemanticTag.Geography;
            // Heuristic: many distinct text values => high-cardinality (poor grouping key).
            if (distinct >= 50)
                return SemanticTag.HighCardinality;
        }

        return SemanticTag.None;
    }

    private static bool IsBoolean(string v) =>
        v is "true" or "false" or "True" or "False" or "TRUE" or "FALSE";

    private static bool IsNumber(string v) =>
        double.TryParse(v.TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out _)
        || double.TryParse(v.TrimStart('$', '€', '£'), NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    private static bool IsDate(string v) =>
        DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsDateTimeWithTime(string v) =>
        DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
        && (dt.TimeOfDay != TimeSpan.Zero || v.Contains(':'));
}
