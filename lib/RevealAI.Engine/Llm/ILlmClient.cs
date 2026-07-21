namespace RevealAI.Engine.Llm;

/// <summary>
/// Provider-neutral LLM client. Returns the model's raw text response (expected to be JSON, since
/// callers instruct the model to emit JSON). One implementation per provider; the active one is
/// chosen at startup from <see cref="LlmOptions.Provider"/>.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Send a system + user prompt and return the model's text completion.
    /// Implementations request JSON output mode where the provider supports it.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);

    LlmProvider Provider { get; }
}
