using System.Globalization;
using RevealAI.Engine.Spec;

namespace RevealAI.Engine.Schema;

/// <summary>
/// Computes column statistics (cardinality, null fraction, min/max, identifier detection) and the
/// shared identifier heuristic. SQL introspection computes exact stats with queries; uploads and
/// inline sample-row requests use <see cref="ProfileFromSampleRows"/> on the (up to 50) rows.
/// </summary>
public static class DataProfiler
{
    /// <summary>Derive per-column stats from the schema's sample rows (marks estimates).</summary>
    public static void ProfileFromSampleRows(DatasetSchema schema)
    {
        var rows = schema.SampleRows;
        schema.RowCount = rows.Count;
        schema.StatsAreEstimates = true;
        if (rows.Count == 0) return;

        foreach (var col in schema.Columns)
        {
            var values = rows
                .Select(r => r.TryGetValue(col.Name, out var v) ? v : null)
                .ToList();
            var nonNull = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();

            col.NonNullCount = nonNull.Count;
            col.DistinctCount = nonNull.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            col.NullFraction = (double)(rows.Count - nonNull.Count) / rows.Count;

            if (col.DataType == DataType.Number)
            {
                var nums = nonNull
                    .Select(v => double.TryParse(v.TrimStart('$', '€', '£').TrimEnd('%'),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? (double?)d : null)
                    .Where(d => d.HasValue).Select(d => d!.Value).ToList();
                if (nums.Count > 0)
                {
                    col.Min = nums.Min().ToString(CultureInfo.InvariantCulture);
                    col.Max = nums.Max().ToString(CultureInfo.InvariantCulture);
                }
                col.IsInteger = nonNull.Count > 0 && nonNull.All(v => !v.Contains('.')
                    && long.TryParse(v, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _));
            }
            else if (col.IsTemporal)
            {
                var dates = nonNull
                    .Select(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? (DateTime?)dt : null)
                    .Where(d => d.HasValue).Select(d => d!.Value).ToList();
                if (dates.Count > 0)
                {
                    col.Min = dates.Min().ToString("yyyy-MM-dd");
                    col.Max = dates.Max().ToString("yyyy-MM-dd");
                }
            }
        }

        Classify(schema);
    }

    /// <summary>
    /// Classify every column as identifier and/or categorical code, using already-populated stats.
    /// </summary>
    public static void Classify(DatasetSchema schema)
    {
        var exact = !schema.StatsAreEstimates;
        foreach (var col in schema.Columns)
        {
            col.IsLikelyIdentifier = IsLikelyIdentifier(col, schema.RowCount);
            col.IsLikelyCategorical = !col.IsLikelyIdentifier && IsLikelyCategorical(col, schema.RowCount, exact);
        }
    }

    /// <summary>
    /// An integer numeric column that is really a categorical code/ordinal: by name (status/type/
    /// rating/tier/…), or — only with exact stats — a small set of distinct small integers that
    /// isn't an additive quantity.
    /// </summary>
    public static bool IsLikelyCategorical(ColumnSchema col, long? rowCount, bool exactStats)
    {
        if (col.DataType != DataType.Number || !col.IsInteger)
            return false;
        if (col.SemanticTag is SemanticTag.Currency or SemanticTag.Percentage
            or SemanticTag.Latitude or SemanticTag.Longitude)
            return false;
        if (ColumnSemantics.IsAdditiveName(col.Name) || ColumnSemantics.IsRatioName(col.Name))
            return false; // it's a measure

        if (ColumnSemantics.IsCodeName(col.Name))
            return true;

        // Unnamed small integer code set — only trust this with exact, full-table statistics.
        return exactStats
               && rowCount is long rc && rc >= 30
               && col.DistinctCount is int d && d is >= 2 and <= 12;
    }

    /// <summary>
    /// An identifier is: a column whose name reads like a key/id/code, OR an integer column whose
    /// distinct count is ≈ the row count (so each value is essentially unique).
    /// </summary>
    public static bool IsLikelyIdentifier(ColumnSchema col, long? rowCount)
    {
        if (col.DataType is DataType.Date or DataType.DateTime or DataType.Boolean)
            return false;

        var n = col.Name.ToLowerInvariant();
        var nameLooksLikeId =
            n == "id" || n.EndsWith("id") || n.EndsWith("_id") || n.EndsWith("key")
            || n.EndsWith("code") || n.EndsWith("guid") || n.EndsWith("uuid") || n.EndsWith("number");
        if (nameLooksLikeId)
            return true;

        // Integer column where (almost) every value is distinct -> a key, not a measure.
        if (col.DataType == DataType.Number && col.IsInteger
            && rowCount is long rc && rc >= 20
            && col.DistinctCount is int distinct && distinct >= 0.95 * rc)
            return true;

        return false;
    }
}
