using System.Text.Json;

namespace RevealSdk.Services
{
    public sealed record ShareEntry(string SourceId, string DashboardName, DateTimeOffset CreatedAt);

    /// <summary>
    /// GUID → dashboard registry backing the public share links (#9). Persisted at
    /// {ContentRoot}/Reveal/shares.json — deliberately NOT under Data/ (which is a
    /// static-file mount). Lock-guarded; the file is small and rewritten atomically.
    /// </summary>
    public sealed class ShareService
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<Guid, ShareEntry>? _entries;

        public ShareService(IWebHostEnvironment env)
        {
            var dir = Path.Combine(env.ContentRootPath, "Reveal");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "shares.json");
        }

        public Guid Create(string sourceId, string dashboardName)
        {
            lock (_lock)
            {
                var entries = Load();
                var id = Guid.NewGuid();
                entries[id] = new ShareEntry(sourceId, dashboardName, DateTimeOffset.UtcNow);
                Save(entries);
                return id;
            }
        }

        public ShareEntry? Get(Guid id)
        {
            lock (_lock)
            {
                return Load().TryGetValue(id, out var entry) ? entry : null;
            }
        }

        public bool Remove(Guid id)
        {
            lock (_lock)
            {
                var entries = Load();
                var removed = entries.Remove(id);
                if (removed) Save(entries);
                return removed;
            }
        }

        public void RemoveForDashboard(string sourceId, string dashboardName)
        {
            lock (_lock)
            {
                var entries = Load();
                var stale = entries
                    .Where(kv => string.Equals(kv.Value.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(kv.Value.DashboardName, dashboardName, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                if (stale.Count == 0) return;
                foreach (var k in stale) entries.Remove(k);
                Save(entries);
            }
        }

        public void RemoveForSource(string sourceId)
        {
            lock (_lock)
            {
                var entries = Load();
                var stale = entries
                    .Where(kv => string.Equals(kv.Value.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                if (stale.Count == 0) return;
                foreach (var k in stale) entries.Remove(k);
                Save(entries);
            }
        }

        private Dictionary<Guid, ShareEntry> Load()
        {
            if (_entries is not null) return _entries;
            try
            {
                _entries = File.Exists(_path)
                    ? JsonSerializer.Deserialize<Dictionary<Guid, ShareEntry>>(File.ReadAllText(_path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                    : new();
            }
            catch
            {
                _entries = new();
            }
            return _entries;
        }

        private void Save(Dictionary<Guid, ShareEntry> entries)
        {
            _entries = entries;
            File.WriteAllText(_path, JsonSerializer.Serialize(entries,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
