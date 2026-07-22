using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RevealAI.Engine.Llm;

/// <summary>
/// Calls an OpenAI-compatible Chat Completions API. Works with OpenAI and any compatible gateway
/// (e.g. Azure OpenAI front-ends that expose the /chat/completions shape) via <c>BaseUrl</c>.
/// </summary>
public sealed class OpenAiClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public OpenAiClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public LlmProvider Provider => LlmProvider.OpenAI;

    // Reasoning-family models (gpt-5*, o*) reject any non-default temperature with a
    // 400. Once a model refuses it, remember and stop sending it for this process.
    private static volatile bool _omitTemperature;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var (status, raw) = await SendAsync(systemPrompt, userPrompt, includeTemperature: !_omitTemperature, ct);

        if (status == 400 && !_omitTemperature && raw.Contains("temperature", StringComparison.OrdinalIgnoreCase))
        {
            _omitTemperature = true;
            (status, raw) = await SendAsync(systemPrompt, userPrompt, includeTemperature: false, ct);
        }

        if (status is < 200 or >= 300)
            throw new HttpRequestException($"OpenAI API error {status}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private async Task<(int Status, string Raw)> SendAsync(
        string systemPrompt, string userPrompt, bool includeTemperature, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.openai.com/v1" : _options.BaseUrl!.TrimEnd('/');

        var payload = new Dictionary<string, object>
        {
            ["model"] = _options.ResolvedModel,
            ["response_format"] = new { type = "json_object" },
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };
        if (includeTemperature)
            payload["temperature"] = _options.Temperature;

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            _options.ApiKey ?? throw new InvalidOperationException("Llm:ApiKey is required for OpenAI."));

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, raw);
    }
}
