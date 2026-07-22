using System.Text.RegularExpressions;

namespace RevealSdk.Services
{
    /// <summary>
    /// A data source known to the app: one folder under Data/ containing a SQLite
    /// database (and, for Excel-derived sources, the original workbook file).
    /// </summary>
    public sealed record SourceInfo(
        string SourceId,
        string SqlitePath,
        string DashboardsDir,
        string? WorkbookPath)
    {
        /// <summary>"excel-derived" when the source was imported from a workbook.</summary>
        public string Kind => WorkbookPath is null ? "sqlite" : "excel-derived";
    }

    /// <summary>
    /// Registry of data sources using the folder-per-source layout:
    ///   Data/{sourceId}/{sourceId}.sqlite      (+ original .xlsx for excel-derived)
    ///   Dashboards/{sourceId}/*.rdash
    /// Everything that needs a per-source path (Reveal providers, /sql browsing,
    /// AI catalog, dashboards) resolves through this singleton.
    /// </summary>
    public sealed class SourceRegistry
    {
        public const string DefaultSourceId = "northwind";

        private readonly string _dataRoot;
        private readonly string _dashboardsRoot;
        private readonly object _lock = new();
        private List<SourceInfo>? _sources;

        public SourceRegistry(IWebHostEnvironment env)
        {
            _dataRoot = Path.Combine(env.ContentRootPath, "Data");
            _dashboardsRoot = Path.Combine(env.ContentRootPath, "Dashboards");
        }

        public IReadOnlyList<SourceInfo> GetSources()
        {
            lock (_lock)
            {
                _sources ??= Scan();
                return _sources;
            }
        }

        /// <summary>Case-insensitive lookup; null when unknown.</summary>
        public SourceInfo? Find(string? sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return null;
            return GetSources().FirstOrDefault(s =>
                string.Equals(s.SourceId, sourceId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Find or fall back to the default source; throws only when NO sources exist at all.
        /// </summary>
        public SourceInfo Resolve(string? sourceId)
        {
            var found = Find(sourceId) ?? Find(DefaultSourceId) ?? GetSources().FirstOrDefault();
            return found ?? throw new InvalidOperationException(
                "No data sources exist. Add a SQLite database under Data/{sourceId}/ or upload a workbook.");
        }

        /// <summary>Create the folder pair for a new source (does not create the .sqlite).</summary>
        public SourceInfo Create(string sourceId)
        {
            var id = Sanitize(sourceId).ToLowerInvariant();
            var dataDir = Path.Combine(_dataRoot, id);
            var dashDir = Path.Combine(_dashboardsRoot, id);
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(dashDir);
            Refresh();
            return new SourceInfo(id, Path.Combine(dataDir, id + ".sqlite"), dashDir, null);
        }

        /// <summary>Remove a source's data folder (and optionally its dashboards folder).</summary>
        public void Delete(string sourceId, bool deleteDashboards)
        {
            var src = Find(sourceId) ?? throw new DirectoryNotFoundException($"Unknown source '{sourceId}'.");
            var dataDir = Path.GetDirectoryName(src.SqlitePath)!;
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
            if (deleteDashboards && Directory.Exists(src.DashboardsDir))
                Directory.Delete(src.DashboardsDir, recursive: true);
            Refresh();
        }

        public void Refresh()
        {
            lock (_lock) { _sources = null; }
        }

        private List<SourceInfo> Scan()
        {
            var list = new List<SourceInfo>();
            if (!Directory.Exists(_dataRoot)) return list;

            foreach (var dir in Directory.GetDirectories(_dataRoot))
            {
                var id = Path.GetFileName(dir);
                // The canonical DB is {sourceId}.sqlite; tolerate any single .sqlite file.
                var sqlite = File.Exists(Path.Combine(dir, id + ".sqlite"))
                    ? Path.Combine(dir, id + ".sqlite")
                    : Directory.GetFiles(dir, "*.sqlite").FirstOrDefault();
                if (sqlite is null) continue;

                var workbook = Directory.GetFiles(dir, "*.xlsx").Concat(Directory.GetFiles(dir, "*.xls"))
                    .FirstOrDefault();
                var dashDir = Path.Combine(_dashboardsRoot, id);
                Directory.CreateDirectory(dashDir);
                list.Add(new SourceInfo(id, sqlite, dashDir, workbook));
            }
            return list.OrderBy(s => s.SourceId, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Sanitize a user-supplied name into a safe source/table identifier: strip
        /// invalid chars, collapse runs to '_', prefix digit-leading names.
        /// </summary>
        public static string Sanitize(string name)
        {
            var s = Regex.Replace(name?.Trim() ?? string.Empty, @"[^A-Za-z0-9_\- ]+", "_");
            s = Regex.Replace(s, @"[\s_]+", "_").Trim('_', '-');
            if (s.Length == 0) return "source";
            if (char.IsDigit(s[0])) s = "T_" + s;
            return s;
        }
    }
}
