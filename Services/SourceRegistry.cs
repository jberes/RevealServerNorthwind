using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace RevealSdk.Services
{
    /// <summary>Connection settings for a config-defined SQL Server source.</summary>
    public sealed record SqlServerSourceConfig(
        string Host,
        string Database,
        string Username,
        string Password,
        string Schema,
        bool TrustServerCertificate)
    {
        // Built via SqlConnectionStringBuilder so special characters in values
        // (e.g. a password starting with '=') are quoted correctly.
        public string ConnectionString => new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{Host},1433",
            InitialCatalog = Database,
            UserID = Username,
            Password = Password,
            Encrypt = true,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = 30
        }.ConnectionString;
    }

    /// <summary>
    /// A data source known to the app: either one folder under Data/ containing a
    /// SQLite database (optionally Excel-derived), or a config-defined SQL Server
    /// connection. Both get a Data/{sourceId}/ folder for per-source artifacts
    /// (ai-selection.json, ai-metadata.json, questions.json) and a
    /// Dashboards/{sourceId}/ folder.
    /// </summary>
    public sealed record SourceInfo(
        string SourceId,
        string? SqlitePath,
        string DashboardsDir,
        string? WorkbookPath,
        SqlServerSourceConfig? SqlServer = null)
    {
        public string Kind => SqlServer is not null ? "sqlserver"
            : WorkbookPath is null ? "sqlite" : "excel-derived";

        /// <summary>The logical database name (SQL Server db, or the sqlite file stem).</summary>
        public string DatabaseName => SqlServer?.Database
            ?? Path.GetFileNameWithoutExtension(SqlitePath!);

        /// <summary>Folder for per-source artifacts (selection/metadata/questions files).</summary>
        public string DataDir { get; init; } = "";
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
        // Config-defined SQL Server sources (registered once at startup; survive Refresh()).
        private readonly List<SourceInfo> _sqlServerSources = new();

        public SourceRegistry(IWebHostEnvironment env)
        {
            _dataRoot = Path.Combine(env.ContentRootPath, "Data");
            _dashboardsRoot = Path.Combine(env.ContentRootPath, "Dashboards");
        }

        /// <summary>
        /// Register a config-defined SQL Server source. Creates its Data/{id}/ and
        /// Dashboards/{id}/ folders (for AI selection files and dashboards).
        /// </summary>
        public SourceInfo RegisterSqlServer(string sourceId, SqlServerSourceConfig config)
        {
            var id = Sanitize(sourceId).ToLowerInvariant();
            var dataDir = Path.Combine(_dataRoot, id);
            var dashDir = Path.Combine(_dashboardsRoot, id);
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(dashDir);
            var src = new SourceInfo(id, null, dashDir, null, config) { DataDir = dataDir };
            lock (_lock)
            {
                _sqlServerSources.RemoveAll(s => string.Equals(s.SourceId, id, StringComparison.OrdinalIgnoreCase));
                _sqlServerSources.Add(src);
                _sources = null;
            }
            return src;
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
            if (src.SqlServer is not null)
                throw new InvalidOperationException(
                    $"'{src.SourceId}' is defined in appsettings — remove its configuration section instead.");
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
            if (Directory.Exists(_dataRoot))
            {
                foreach (var dir in Directory.GetDirectories(_dataRoot))
                {
                    var id = Path.GetFileName(dir);
                    // Folders belonging to config-defined SQL Server sources carry no .sqlite.
                    if (_sqlServerSources.Any(s => string.Equals(s.SourceId, id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    // The canonical DB is {sourceId}.sqlite; tolerate any single .sqlite file.
                    var sqlite = File.Exists(Path.Combine(dir, id + ".sqlite"))
                        ? Path.Combine(dir, id + ".sqlite")
                        : Directory.GetFiles(dir, "*.sqlite").FirstOrDefault();
                    if (sqlite is null) continue;

                    var workbook = Directory.GetFiles(dir, "*.xlsx").Concat(Directory.GetFiles(dir, "*.xls"))
                        .FirstOrDefault();
                    var dashDir = Path.Combine(_dashboardsRoot, id);
                    Directory.CreateDirectory(dashDir);
                    list.Add(new SourceInfo(id, sqlite, dashDir, workbook) { DataDir = dir });
                }
            }
            list.AddRange(_sqlServerSources);
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
