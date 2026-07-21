using RevealAI.Engine.DataSources;
using RevealAI.Engine.Schema;

namespace RevealAI.Engine.Introspection;

/// <summary>
/// Resolves a configured connection and delegates to the matching <see cref="ISchemaIntrospector"/>
/// to read a live schema. Throws <see cref="NotSupportedException"/> if no introspector handles the
/// connection type (e.g. file/REST connections, which don't introspect).
/// </summary>
public sealed class SchemaIntrospectionService
{
    private readonly ConnectionRegistry _connections;
    private readonly IReadOnlyList<ISchemaIntrospector> _introspectors;

    public SchemaIntrospectionService(ConnectionRegistry connections, IEnumerable<ISchemaIntrospector> introspectors)
    {
        _connections = connections;
        _introspectors = introspectors.ToList();
    }

    public bool CanIntrospect(ConnectionType type) => _introspectors.Any(i => i.CanHandle(type));

    public Task<DatasetSchema> IntrospectAsync(
        string connectionId, string dataset, int sampleRows = 5, CancellationToken ct = default)
        => IntrospectAsync(_connections.Get(connectionId), dataset, sampleRows, ct);

    public Task<List<string>> ListDatasetsAsync(string connectionId, CancellationToken ct = default)
        => ListDatasetsAsync(_connections.Get(connectionId), ct);

    /// <summary>Introspect an inline (multi-tenant) connection supplied on the request.</summary>
    public Task<DatasetSchema> IntrospectAsync(
        ConnectionConfig connection, string dataset, int sampleRows = 5, CancellationToken ct = default)
        => Introspector(connection).IntrospectAsync(connection, dataset, sampleRows, ct);

    public Task<List<string>> ListDatasetsAsync(ConnectionConfig connection, CancellationToken ct = default)
        => Introspector(connection).ListDatasetsAsync(connection, ct);

    private ISchemaIntrospector Introspector(ConnectionConfig connection) =>
        _introspectors.FirstOrDefault(i => i.CanHandle(connection.Type))
            ?? throw new NotSupportedException(
                $"Schema introspection is not supported for connection type '{connection.Type}'.");
}
