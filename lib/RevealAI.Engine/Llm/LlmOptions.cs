using System.Text.Json.Serialization;

namespace RevealAI.Engine.Llm;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmProvider
{
    Anthropic,
    OpenAI,
    Ollama
}

/// <summary>
/// LLM configuration, bound from the "Llm" config section. The developer only needs to set
/// <see cref="Provider"/> and (for hosted providers) <see cref="ApiKey"/>; everything else has
/// sensible defaults.
/// </summary>
public sealed class LlmOptions
{
    public LlmProvider Provider { get; set; } = LlmProvider.Anthropic;

    /// <summary>API key for Anthropic / OpenAI. Not required for local Ollama.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model id. Defaults per provider if left empty (see <c>DefaultModel</c>).</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Override the API base URL. Needed for Ollama (defaults to http://localhost:11434) or for
    /// Azure/OpenAI-compatible gateways. Leave empty to use the provider default.
    /// </summary>
    public string? BaseUrl { get; set; }

    public int MaxTokens { get; set; } = 4096;

    public double Temperature { get; set; } = 0;

    public string DefaultModel => Provider switch
    {
        LlmProvider.Anthropic => "claude-sonnet-4-6",
        LlmProvider.OpenAI => "gpt-4o-mini",
        LlmProvider.Ollama => "llama3.1",
        _ => "claude-sonnet-4-6"
    };

    public string ResolvedModel => string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model!;
}
