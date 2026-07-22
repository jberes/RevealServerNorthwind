using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RevealAI.Engine.Llm;

namespace RevealSdk.Services
{
    public sealed record SuggestedQuestions(
        DateTimeOffset GeneratedAt,
        string Source,              // "llm" | "fallback"
        List<string> Questions);

    /// <summary>
    /// Generates ~6 starter questions for the AI Assistant per data source by showing
    /// the source's schema (AI-selected tables, or all tables when nothing is selected
    /// yet) to the LLM. Cached at Data/{sourceId}/questions.json; regenerated when the
    /// AI catalog selection changes. Falls back to deterministic template questions
    /// when no API key is configured or the LLM call fails.
    /// </summary>
    public sealed class SuggestedQuestionsService
    {
        private const string CacheFileName = "questions.json";
        private const int QuestionCount = 6;

        private readonly SourceRegistry _registry;
        private readonly AiCatalogService _catalog;
        private readonly ILlmClient _llm;
        private readonly LlmOptions _llmOptions;
        // Per-source locks so parallel first-GETs don't double-call the LLM.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        public SuggestedQuestionsService(
            SourceRegistry registry, AiCatalogService catalog,
            ILlmClient llm, IOptions<LlmOptions> llmOptions)
        {
            _registry = registry;
            _catalog = catalog;
            _llm = llm;
            _llmOptions = llmOptions.Value;
        }

        public async Task<SuggestedQuestions> GetAsync(string sourceId, CancellationToken ct = default)
        {
            var cached = ReadCache(sourceId);
            if (cached is not null) return cached;
            return await RegenerateAsync(sourceId, ct);
        }

        public async Task<SuggestedQuestions> RegenerateAsync(string sourceId, CancellationToken ct = default)
        {
            var gate = _locks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Another caller may have finished while we waited.
                var cached = ReadCache(sourceId);
                if (cached is not null && (DateTimeOffset.UtcNow - cached.GeneratedAt) < TimeSpan.FromSeconds(30))
                    return cached;

                var src = _registry.Resolve(sourceId);
                var schema = DescribeSchema(src);

                SuggestedQuestions result;
                if (schema.Count == 0)
                {
                    result = new SuggestedQuestions(DateTimeOffset.UtcNow, "fallback",
                        new List<string> { "What data is available in this source?" });
                }
                else if (string.IsNullOrWhiteSpace(_llmOptions.ApiKey) && _llmOptions.Provider != LlmProvider.Ollama)
                {
                    result = new SuggestedQuestions(DateTimeOffset.UtcNow, "fallback", TemplateQuestions(schema));
                }
                else
                {
                    try
                    {
                        result = new SuggestedQuestions(DateTimeOffset.UtcNow, "llm",
                            await AskLlmAsync(src.SourceId, schema, ct));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Suggestions] LLM failed for '{sourceId}': {ex.Message} — using templates.");
                        result = new SuggestedQuestions(DateTimeOffset.UtcNow, "fallback", TemplateQuestions(schema));
                    }
                }

                WriteCache(src, result);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Drop the cache so the next GET regenerates (called when the AI selection changes).</summary>
        public void Invalidate(string sourceId)
        {
            var src = _registry.Find(sourceId);
            if (src is null) return;
            try
            {
                var path = CachePath(src);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best-effort */ }
        }

        // ---- schema snapshot -----------------------------------------------------

        private sealed record TableSchema(string Name, List<(string Column, string Type)> Columns);

        private List<TableSchema> DescribeSchema(SourceInfo src)
        {
            var tables = _catalog.GetSelection(src.SourceId);
            if (tables.Count == 0)
                tables = AiCatalogService.ListObjects(src.SqlitePath).ToList();

            var result = new List<TableSchema>();
            try
            {
                using var conn = new SqliteConnection(AiCatalogService.ReadOnlyConnString(src.SqlitePath));
                conn.Open();
                foreach (var t in tables.Take(10)) // cap prompt size
                {
                    var cols = new List<(string, string)>();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"PRAGMA table_info(\"{t.Replace("\"", "\"\"")}\")";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        cols.Add((r.GetString(1), r.IsDBNull(2) ? "any" : r.GetString(2)));
                    if (cols.Count > 0) result.Add(new TableSchema(t, cols));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Suggestions] Could not read schema for '{src.SourceId}': {ex.Message}");
            }
            return result;
        }

        // ---- LLM path --------------------------------------------------------------

        private async Task<List<string>> AskLlmAsync(string sourceId, List<TableSchema> schema, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Data source: {sourceId}");
            foreach (var t in schema)
            {
                sb.Append("- ").Append(t.Name).Append(": ");
                sb.AppendLine(string.Join(", ", t.Columns.Select(c => $"{c.Column} ({c.Type})")));
            }

            // NOTE: the engine's OpenAI client forces response_format=json_object, so the
            // prompt MUST ask for a JSON object.
            var system =
                "You write starter questions for a business-intelligence chat assistant. " +
                "Given a database schema, produce exactly " + QuestionCount + " short, concrete, varied questions " +
                "a business user would ask (totals, rankings, trends over time when a date column exists, breakdowns). " +
                "Use the actual table/column names' business meaning; keep each under 60 characters when possible. " +
                "Respond with ONLY a JSON object: {\"questions\": [\"...\"]}";

            var raw = await _llm.CompleteAsync(system, sb.ToString(), ct);
            using var doc = JsonDocument.Parse(raw);
            var questions = doc.RootElement.GetProperty("questions").EnumerateArray()
                .Select(q => q.GetString())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q!.Trim())
                .Take(QuestionCount)
                .ToList();
            if (questions.Count == 0) throw new InvalidOperationException("LLM returned no questions.");
            return questions;
        }

        // ---- deterministic fallback -------------------------------------------------

        private static List<string> TemplateQuestions(List<TableSchema> schema)
        {
            static bool IsNumeric(string t) =>
                t.Contains("int", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("real", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("num", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("money", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("dec", StringComparison.OrdinalIgnoreCase);
            static bool IsDate(string t) =>
                t.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("time", StringComparison.OrdinalIgnoreCase);
            static bool IsText(string t) =>
                t.Contains("char", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("text", StringComparison.OrdinalIgnoreCase);

            var questions = new List<string>();
            foreach (var t in schema)
            {
                var num = t.Columns.FirstOrDefault(c => IsNumeric(c.Type) &&
                    !c.Column.EndsWith("ID", StringComparison.OrdinalIgnoreCase)).Column;
                var text = t.Columns.FirstOrDefault(c => IsText(c.Type)).Column;
                var date = t.Columns.FirstOrDefault(c => IsDate(c.Type)).Column;

                if (num is not null && text is not null)
                    questions.Add($"What is the total {num} by {text}?");
                if (num is not null && date is not null)
                    questions.Add($"How has {num} changed over time?");
                if (num is not null)
                    questions.Add($"Show the top 10 {t.Name} by {num}");
                if (text is not null)
                    questions.Add($"How many {t.Name} are there per {text}?");
                questions.Add($"Build a dashboard summarizing {t.Name}");
                if (questions.Count >= QuestionCount) break;
            }
            if (questions.Count == 0) questions.Add("What data is available in this source?");
            return questions.Distinct().Take(QuestionCount).ToList();
        }

        // ---- cache -------------------------------------------------------------------

        private static string CachePath(SourceInfo src) =>
            Path.Combine(Path.GetDirectoryName(src.SqlitePath)!, CacheFileName);

        private SuggestedQuestions? ReadCache(string sourceId)
        {
            var src = _registry.Find(sourceId);
            if (src is null) return null;
            var path = CachePath(src);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<SuggestedQuestions>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(SourceInfo src, SuggestedQuestions value)
        {
            try
            {
                File.WriteAllText(CachePath(src), JsonSerializer.Serialize(value,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort */ }
        }
    }
}
