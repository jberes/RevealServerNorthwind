using RevealAI.Engine.DataSources;
using RevealAI.Engine.Schema;

namespace RevealAI.Engine.Introspection;

/// <summary>
/// Connects to a live data source and reads its schema (and optional sample rows). One
/// implementation per connector family; the coordinator picks the first that can handle a type.
/// </summary>
public interface ISchemaIntrospector
{
    bool CanHandle(ConnectionType type);

    Task<DatasetSchema> IntrospectAsync(
        ConnectionConfig connection, string dataset, int sampleRows, CancellationToken ct = default);

    /// <summary>List the datasets (tables/views) available in the connection.</summary>
    Task<List<string>> ListDatasetsAsync(ConnectionConfig connection, CancellationToken ct = default);
}
