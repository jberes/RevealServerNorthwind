using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RevealAI.Engine.Llm;

/// <summary>Calls a local Ollama server (https://github.com/ollama/ollama/blob/main/docs/api.md).</summary>
public sealed class OllamaClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public OllamaClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public LlmProvider Provider => LlmProvider.Ollama;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "http://localhost:11434" : _options.BaseUrl!.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
        {
            Content = JsonContent.Create(new
            {
                model = _options.ResolvedModel,
                stream = false,
                format = "json",
                options = new { temperature = _options.Temperature },
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            })
        };

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama API error {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
