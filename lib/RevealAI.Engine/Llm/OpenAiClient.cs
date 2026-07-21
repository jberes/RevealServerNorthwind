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

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.openai.com/v1" : _options.BaseUrl!.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = _options.ResolvedModel,
                temperature = _options.Temperature,
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            _options.ApiKey ?? throw new InvalidOperationException("Llm:ApiKey is required for OpenAI."));

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI API error {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
