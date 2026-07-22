using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Reveal.Sdk.Dom;
using RevealAI.Engine.Llm;

namespace RevealSdk.Services
{
    public sealed record VizFieldFact(string Role, string Name, string? Aggregation, string? Sorting);
    public sealed record VizFact(
        string Id, string Title, string ChartType, string? Table,
        List<VizFieldFact> Fields, List<string> HiddenFields, List<string> FilterFields);
    public sealed record DashboardFilterFact(string Title, string Kind, List<string> SelectedValues);
    public sealed record DashboardAnalysisResult(
        string DashboardName, string SourceId, string Title,
        List<DashboardFilterFact> Filters, List<VizFact> Visualizations,
        string? Narrative, string NarrativeSource, DateTimeOffset GeneratedAt);

    /// <summary>
    /// Produces the per-visualization breakdown shown on the share viewer (#9):
    /// what each viz is, the table/sheet it reads, its bound fields/aggregations,
    /// hidden fields, and filters — plus an LLM-written narrative.
    ///
    /// Extraction is a DUAL READ of the .rdash:
    ///  - typed Reveal.Sdk.Dom (RdashDocument) for dashboard title, viz identity,
    ///    chart types, and dashboard/viz filters;
    ///  - the raw Dashboard.json inside the rdash ZIP for everything the typed API
    ///    hides (DataSourceItem.Properties.Table is internal in the DOM, and the
    ///    role-specific binding shapes vary per viz type) — bindings are collected
    ///    generically by scanning VisualizationDataSpec for FieldName references.
    ///
    /// Results cache to Dashboards/{sourceId}/{name}.analysis.json keyed by the
    /// rdash's LastWriteTimeUtc; a save/delete invalidates via the endpoints.
    /// </summary>
    public sealed class DashboardAnalyzer
    {
        private readonly SourceRegistry _registry;
        private readonly ILlmClient _llm;
        private readonly LlmOptions _llmOptions;

        public DashboardAnalyzer(SourceRegistry registry, ILlmClient llm,
            Microsoft.Extensions.Options.IOptions<LlmOptions> llmOptions)
        {
            _registry = registry;
            _llm = llm;
            _llmOptions = llmOptions.Value;
        }

        public async Task<DashboardAnalysisResult> AnalyzeAsync(string sourceId, string dashboardName, CancellationToken ct = default)
        {
            var src = _registry.Resolve(sourceId);
            var rdashPath = Path.Combine(src.DashboardsDir, dashboardName + ".rdash");
            if (!File.Exists(rdashPath))
                throw new FileNotFoundException($"Dashboard '{dashboardName}' was not found.", rdashPath);

            // ---- cache ----
            var cachePath = Path.ChangeExtension(rdashPath, ".analysis.json");
            var stamp = File.GetLastWriteTimeUtc(rdashPath);
            var cached = ReadCache(cachePath, stamp);
            if (cached is not null) return cached;

            var facts = ExtractFacts(src.SourceId, dashboardName, rdashPath);

            string? narrative = null;
            var narrativeSource = "facts-only";
            if (!string.IsNullOrWhiteSpace(_llmOptions.ApiKey) || _llmOptions.Provider == LlmProvider.Ollama)
            {
                try
                {
                    narrative = await WriteNarrativeAsync(facts, ct);
                    narrativeSource = "llm";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Analysis] Narrative failed for '{dashboardName}': {ex.Message}");
                }
            }

            var result = facts with { Narrative = narrative, NarrativeSource = narrativeSource, GeneratedAt = DateTimeOffset.UtcNow };
            WriteCache(cachePath, stamp, result);
            return result;
        }

        // ---- extraction ------------------------------------------------------------

        private static DashboardAnalysisResult ExtractFacts(string sourceId, string dashboardName, string rdashPath)
        {
            // Typed pass: identity, chart types, filters.
            var doc = RdashDocument.Load(rdashPath);
            var typedById = doc.Visualizations.ToDictionary(v => v.Id, v => v);

            var filters = new List<DashboardFilterFact>();
            foreach (var f in doc.Filters)
            {
                var kind = f.GetType().Name.Contains("Date", StringComparison.OrdinalIgnoreCase) ? "date" : "data";
                filters.Add(new DashboardFilterFact(f.Title ?? kind, kind, new List<string>()));
            }

            // Raw pass: table names + generic binding scan.
            using var zip = ZipFile.OpenRead(rdashPath);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("Dashboard.json", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("The .rdash contains no Dashboard.json.");
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;

            var vizFacts = new List<VizFact>();
            if (root.TryGetProperty("Widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
            {
                foreach (var widget in widgets.EnumerateArray())
                {
                    var id = GetString(widget, "Id") ?? "";
                    var title = GetString(widget, "Title") ?? "(untitled)";
                    var chartType = typedById.TryGetValue(id, out var typed)
                        ? typed.ChartType.ToString()
                        : GetString(GetProp(widget, "VisualizationSettings"), "VisualizationType") ?? "Unknown";

                    // Table: DataSpec.DataSourceItem.Properties.Table
                    string? table = null;
                    var dataSpec = GetProp(widget, "DataSpec");
                    var dsi = GetProp(dataSpec, "DataSourceItem");
                    var dsiProps = GetProp(dsi, "Properties");
                    if (dsiProps.ValueKind == JsonValueKind.Object)
                        table = GetString(dsiProps, "Table");
                    table ??= GetString(dsi, "Title");

                    // Bound fields: scan VisualizationDataSpec for FieldName refs, grouped
                    // by the array key they appear under (Columns / Rows / Values / ...).
                    var fields = new List<VizFieldFact>();
                    var vds = GetProp(widget, "VisualizationDataSpec");
                    if (vds.ValueKind == JsonValueKind.Object)
                        CollectBindings(vds, "Field", fields);

                    // Hidden fields: DataSpec.Fields entries with IsHidden=true.
                    var hidden = new List<string>();
                    var allFields = GetProp(dataSpec, "Fields");
                    if (allFields.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in allFields.EnumerateArray())
                        {
                            if (f.ValueKind == JsonValueKind.Object
                                && f.TryGetProperty("IsHidden", out var h) && h.ValueKind == JsonValueKind.True)
                            {
                                var n = GetString(f, "FieldName");
                                if (n is not null) hidden.Add(n);
                            }
                        }
                    }

                    // Per-viz filters (typed) — VisualizationFilter exposes FieldName only.
                    var vizFilters = new List<string>();
                    if (typedById.TryGetValue(id, out var tviz) && tviz.Filters is not null)
                    {
                        foreach (var vf in tviz.Filters)
                        {
                            var fn = vf?.GetType().GetProperty("FieldName")?.GetValue(vf)?.ToString();
                            if (!string.IsNullOrWhiteSpace(fn)) vizFilters.Add(fn!);
                        }
                    }

                    vizFacts.Add(new VizFact(id, title, chartType, table, fields, hidden, vizFilters));
                }
            }

            // Dashboard filter selected values from the raw GlobalFilters (typed hides them).
            if (root.TryGetProperty("GlobalFilters", out var gfilters) && gfilters.ValueKind == JsonValueKind.Array)
            {
                var rawFilters = new List<DashboardFilterFact>();
                foreach (var gf in gfilters.EnumerateArray())
                {
                    var t = GetString(gf, "Title") ?? "Filter";
                    var kind = (GetString(gf, "_type") ?? "").Contains("Date", StringComparison.OrdinalIgnoreCase) ? "date" : "data";
                    var selected = new List<string>();
                    if (gf.TryGetProperty("SelectedItems", out var sel) && sel.ValueKind == JsonValueKind.Array)
                        foreach (var s in sel.EnumerateArray())
                            selected.Add(s.ToString());
                    rawFilters.Add(new DashboardFilterFact(t, kind, selected));
                }
                if (rawFilters.Count > 0) filters = rawFilters;
            }

            return new DashboardAnalysisResult(dashboardName, sourceId, doc.Title ?? dashboardName,
                filters, vizFacts, null, "facts-only", DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Recursively collect field bindings: any object with a FieldName is a field
        /// reference; its role is the nearest enclosing ARRAY property name (Columns,
        /// Rows, Values, ...), and aggregation/sorting are read from the object itself.
        /// </summary>
        private static void CollectBindings(JsonElement element, string role, List<VizFieldFact> into)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("FieldName", out var fn) && fn.ValueKind == JsonValueKind.String)
                    {
                        var name = fn.GetString()!;
                        var agg = GetString(element, "AggregationType") ?? GetString(element, "Function");
                        var sort = GetString(element, "Sorting");
                        if (sort == "None") sort = null;
                        if (!into.Any(f => f.Name == name && f.Role == role))
                            into.Add(new VizFieldFact(role, name, agg, sort));
                    }
                    foreach (var prop in element.EnumerateObject())
                    {
                        var nextRole = prop.Value.ValueKind == JsonValueKind.Array ? prop.Name : role;
                        CollectBindings(prop.Value, nextRole, into);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        CollectBindings(item, role, into);
                    break;
            }
        }

        // ---- narrative ---------------------------------------------------------------

        private async Task<string> WriteNarrativeAsync(DashboardAnalysisResult facts, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Dashboard: {facts.Title}");
            if (facts.Filters.Count > 0)
                sb.AppendLine("Dashboard filters: " + string.Join("; ",
                    facts.Filters.Select(f => $"{f.Title} ({f.Kind}{(f.SelectedValues.Count > 0 ? $", selected: {string.Join(",", f.SelectedValues)}" : "")})")));
            else
                sb.AppendLine("Dashboard filters: none");
            var i = 0;
            foreach (var v in facts.Visualizations)
            {
                i++;
                sb.AppendLine($"{i}. \"{v.Title}\" — {v.ChartType}, table: {v.Table ?? "unknown"}");
                if (v.Fields.Count > 0)
                    sb.AppendLine("   bindings: " + string.Join("; ",
                        v.Fields.Select(f => $"{f.Role}: {f.Name}{(f.Aggregation is not null ? $" ({f.Aggregation})" : "")}{(f.Sorting is not null ? $", sort {f.Sorting}" : "")}")));
                if (v.HiddenFields.Count > 0)
                    sb.AppendLine($"   hidden fields: {string.Join(", ", v.HiddenFields)}");
                if (v.FilterFields.Count > 0)
                    sb.AppendLine($"   viz filters on: {string.Join(", ", v.FilterFields)}");
            }

            // NOTE: the engine's OpenAI client forces response_format=json_object.
            var system =
                "You are a BI analyst. Given the structured facts of a dashboard, write a clear breakdown for a " +
                "business user, in markdown: one numbered section per visualization — '1. Title (ChartType, table: X)' " +
                "followed by 1-3 sentences covering what it shows, which fields/aggregations it uses, sorting, and " +
                "filters; mention hidden fields when present. Close with a short 'Across the whole dashboard' paragraph " +
                "summarizing filters and data usage. Be factual — never invent fields that are not listed. " +
                "Respond with ONLY a JSON object: {\"narrative\": \"...markdown...\"}";

            var raw = await _llm.CompleteAsync(system, sb.ToString(), ct);
            using var doc = JsonDocument.Parse(raw);
            var narrative = doc.RootElement.GetProperty("narrative").GetString();
            if (string.IsNullOrWhiteSpace(narrative))
                throw new InvalidOperationException("LLM returned an empty narrative.");
            return narrative!;
        }

        // ---- cache ---------------------------------------------------------------------

        private sealed record CacheEnvelope(DateTime RdashLastWriteUtc, DashboardAnalysisResult Result);

        private static DashboardAnalysisResult? ReadCache(string path, DateTime stamp)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var envelope = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return envelope is not null && envelope.RdashLastWriteUtc == stamp ? envelope.Result : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(string path, DateTime stamp, DashboardAnalysisResult result)
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new CacheEnvelope(stamp, result),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort */ }
        }

        // ---- json helpers ---------------------------------------------------------------

        private static JsonElement GetProp(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
                ? v
                : default;

        private static string? GetString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
