using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace RevealAI.Engine.DataSources;

/// <summary>Options container bound from the "Connections" configuration section.</summary>
public sealed class ConnectionOptions
{
    public List<ConnectionConfig> Connections { get; set; } = new();
}

/// <summary>
/// Looks up connections by id. Seeded from configuration and extensible at runtime (e.g. uploaded
/// files register a transient connection). Registered as a singleton; thread-safe.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConnectionConfig> _byId;

    public ConnectionRegistry(IEnumerable<ConnectionConfig> connections)
    {
        _byId = new ConcurrentDictionary<string, ConnectionConfig>(
            connections
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => new KeyValuePair<string, ConnectionConfig>(c.Id, c)),
            StringComparer.OrdinalIgnoreCase);
    }

    public ConnectionRegistry(IOptions<ConnectionOptions> options)
        : this(options.Value.Connections) { }

    public IReadOnlyCollection<ConnectionConfig> All => _byId.Values.ToList();

    public ConnectionConfig Get(string id)
    {
        if (_byId.TryGetValue(id, out var config))
            return config;
        throw new KeyNotFoundException(
            $"No connection configured with id '{id}'. Known ids: {string.Join(", ", _byId.Keys)}");
    }

    public bool TryGet(string id, out ConnectionConfig config) => _byId.TryGetValue(id, out config!);

    /// <summary>Add or replace a connection at runtime (e.g. for an uploaded file).</summary>
    public void AddOrUpdate(ConnectionConfig connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Id))
            throw new ArgumentException("Connection must have an Id.", nameof(connection));
        _byId[connection.Id] = connection;
    }

    public bool Remove(string id) => _byId.TryRemove(id, out _);
}
