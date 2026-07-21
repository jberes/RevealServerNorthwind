using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RevealAI.Engine.Llm;

/// <summary>Calls the Anthropic Messages API (https://docs.anthropic.com/en/api/messages).</summary>
public sealed class AnthropicClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public AnthropicClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public LlmProvider Provider => LlmProvider.Anthropic;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.anthropic.com" : _options.BaseUrl!.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = _options.ResolvedModel,
                max_tokens = _options.MaxTokens,
                temperature = _options.Temperature,
                system = systemPrompt,
                messages = new[] { new { role = "user", content = userPrompt } }
            })
        };
        request.Headers.Add("x-api-key", _options.ApiKey ?? throw new InvalidOperationException("Llm:ApiKey is required for Anthropic."));
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API error {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        // content is an array of blocks; concatenate the text blocks.
        var sb = new System.Text.StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                sb.Append(block.GetProperty("text").GetString());
        }
        return sb.ToString();
    }
}
