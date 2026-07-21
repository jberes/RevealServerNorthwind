namespace RevealAI.Engine.Schema;

/// <summary>
/// The schema of a dataset plus optional sample rows. This is the primary input the caller
/// supplies (or that <see cref="SchemaInference"/> derives from sample rows alone).
/// </summary>
public sealed class DatasetSchema
{
    /// <summary>Friendly name of the dataset (table / sheet / endpoint).</summary>
    public string Name { get; set; } = "Dataset";

    public List<ColumnSchema> Columns { get; set; } = new();

    /// <summary>Up to a handful of representative rows (column name -> value as string).</summary>
    public List<Dictionary<string, string?>> SampleRows { get; set; } = new();

    /// <summary>Total row count (exact when introspected; sample size when estimated).</summary>
    public long? RowCount { get; set; }

    /// <summary>True when statistics were derived from sample rows rather than the full dataset.</summary>
    public bool StatsAreEstimates { get; set; }

    public ColumnSchema? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ColumnSchema> Measures => Columns.Where(c => c.IsMeasure);
    public IEnumerable<ColumnSchema> Dimensions => Columns.Where(c => c.IsDimension);
    public IEnumerable<ColumnSchema> Temporals => Columns.Where(c => c.IsTemporal);
}
