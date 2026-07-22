using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RevealSdk.Services
{
    /// <summary>
    /// Owns which tables/views each source exposes to the Reveal AI assistant.
    ///
    /// The per-source selection is persisted at Data/{sourceId}/ai-selection.json and
    /// compiled into the single metadata catalog file (Reveal/Metadata/catalog.json,
    /// "Restricted" discovery) that AddRevealAI().UseMetadataCatalogFile(...) consumes.
    /// FileMetadataCatalogProvider re-reads that file on every call, so rewriting it at
    /// runtime takes effect without a restart; callers are responsible for triggering
    /// IMetadataService regeneration afterwards (see the /ai/catalog endpoints).
    /// </summary>
    public sealed class AiCatalogService
    {
        private const string SelectionFileName = "ai-selection.json";

        private readonly SourceRegistry _registry;
        private readonly string _catalogPath;
        private readonly object _lock = new();

        private sealed record SelectionFile(List<string> Tables);

        public AiCatalogService(SourceRegistry registry, IWebHostEnvironment env)
        {
            _registry = registry;
            var catalogDir = Path.Combine(env.ContentRootPath, "Reveal", "Metadata");
            Directory.CreateDirectory(catalogDir);
            _catalogPath = Path.Combine(catalogDir, "catalog.json");
        }

        public string CatalogPath => _catalogPath;

        /// <summary>The tables/views currently selected for the AI on a source (empty when none).</summary>
        public List<string> GetSelection(string sourceId)
        {
            var src = _registry.Find(sourceId);
            if (src is null) return new List<string>();
            var file = SelectionPath(src);
            if (!File.Exists(file)) return new List<string>();
            try
            {
                var parsed = JsonSerializer.Deserialize<SelectionFile>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return parsed?.Tables ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Persist a new selection (validated against the source's actual tables/views)
        /// and rebuild catalog.json. Returns the validated, deduplicated selection.
        /// </summary>
        public List<string> SetSelection(string sourceId, IEnumerable<string> tables)
        {
            var src = _registry.Find(sourceId)
                      ?? throw new DirectoryNotFoundException($"Unknown source '{sourceId}'.");

            var actual = ListObjects(src.SqlitePath);
            var validated = tables
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => actual.Contains(t))
                .ToList();

            lock (_lock)
            {
                File.WriteAllText(SelectionPath(src), JsonSerializer.Serialize(
                    new SelectionFile(validated), new JsonSerializerOptions { WriteIndented = true }));
            }
            RebuildCatalogJson();
            return validated;
        }

        /// <summary>Seed a source's selection only when no selection file exists yet.</summary>
        public void SeedSelectionIfMissing(string sourceId, IEnumerable<string> tables)
        {
            var src = _registry.Find(sourceId);
            if (src is null || File.Exists(SelectionPath(src))) return;
            SetSelection(sourceId, tables);
        }

        /// <summary>
        /// Rewrite catalog.json from every source's persisted selection: one Restricted
        /// SQLITE datasource per source with a non-empty selection. The datasource Id AND
        /// the database Name both equal the sourceId — DataSourceProvider resolves the
        /// .sqlite path from that Id during AI metadata generation (whose bare user
        /// context carries no properties).
        /// </summary>
        public void RebuildCatalogJson()
        {
            lock (_lock)
            {
                var datasources = new List<object>();
                foreach (var src in _registry.GetSources())
                {
                    var selection = GetSelection(src.SourceId);
                    if (selection.Count == 0) continue;

                    Dictionary<string, List<string>> columns;
                    try { columns = DiscoverColumns(src.SqlitePath, selection.ToArray()); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AI Catalog] {src.SourceId}: could not read columns: {ex.Message}");
                        columns = new(StringComparer.OrdinalIgnoreCase);
                    }

                    datasources.Add(new
                    {
                        Id = src.SourceId,
                        Provider = "SQLITE",
                        Databases = new object[]
                        {
                            new
                            {
                                Name = Path.GetFileNameWithoutExtension(src.SqlitePath),
                                DiscoveryMode = "Restricted",
                                Tables = selection.Select(t => new
                                {
                                    Name = t,
                                    Fields = (columns.TryGetValue(t, out var cols) ? cols : new List<string>())
                                        .Select(c => new { Name = c })
                                        .ToArray()
                                }).ToArray()
                            }
                        }
                    });
                }

                File.WriteAllText(_catalogPath, JsonSerializer.Serialize(
                    new { Datasources = datasources },
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"[AI Catalog] catalog.json rebuilt: {datasources.Count} datasource(s).");
            }
        }

        /// <summary>All table/view names in a SQLite file (excluding sqlite_ internals).</summary>
        public static HashSet<string> ListObjects(string sqlitePath)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqliteConnection(ReadOnlyConnString(sqlitePath));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT name FROM sqlite_master
                                WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        }

        /// <summary>Column names per table/view via PRAGMA table_info (works for both).</summary>
        public static Dictionary<string, List<string>> DiscoverColumns(string sqlitePath, string[] names)
        {
            var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (names is null || names.Length == 0) return columns;
            foreach (var n in names) columns[n] = new List<string>();

            using var conn = new SqliteConnection(ReadOnlyConnString(sqlitePath));
            conn.Open();
            foreach (var n in names)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info(\"{n.Replace("\"", "\"\"")}\")";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    columns[n].Add(r.GetString(1)); // ordinal 1 = column name
            }
            return columns;
        }

        public static string ReadOnlyConnString(string dbPath) =>
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ConnectionString;

        private static string SelectionPath(SourceInfo src) =>
            Path.Combine(Path.GetDirectoryName(src.SqlitePath)!, SelectionFileName);
    }
}
