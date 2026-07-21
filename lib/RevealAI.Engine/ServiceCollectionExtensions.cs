using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RevealAI.Engine.Compilation;
using RevealAI.Engine.DataSources;
using RevealAI.Engine.Llm;
using RevealAI.Engine.Recommendation;

namespace RevealAI.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the engine, binding the "Llm" and "Connections" config sections. Use this when you
    /// want connections pre-configured in appsettings.
    /// </summary>
    public static IServiceCollection AddRevealAiEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection("Llm"));
        var connections = configuration.GetSection("Connections").Get<List<ConnectionConfig>>() ?? new();
        return services.AddRevealAiEngineCore(connections);
    }

    /// <summary>
    /// Register the engine with the LLM options supplied by the host app (e.g. the AI key from your
    /// own configuration). Connections are passed inline per request — no appsettings needed.
    /// Pass an empty/no-ApiKey <paramref name="llm"/> to use the deterministic heuristic recommender.
    /// </summary>
    public static IServiceCollection AddRevealAiEngine(
        this IServiceCollection services, LlmOptions llm, IEnumerable<ConnectionConfig>? connections = null)
    {
        services.AddSingleton<IConfigureOptions<LlmOptions>>(new ConfigureNamedOptions<LlmOptions>(string.Empty, o =>
        {
            o.Provider = llm.Provider;
            o.ApiKey = llm.ApiKey;
            o.Model = llm.Model;
            o.BaseUrl = llm.BaseUrl;
            o.MaxTokens = llm.MaxTokens;
            o.Temperature = llm.Temperature;
        }));
        return services.AddRevealAiEngineCore(connections ?? Enumerable.Empty<ConnectionConfig>());
    }

    private static IServiceCollection AddRevealAiEngineCore(this IServiceCollection services, IEnumerable<ConnectionConfig> connections)
    {
        services.AddOptions();
        services.AddSingleton(new ConnectionRegistry(connections));

        services.AddSingleton<DataSourceResolver>();
        services.AddSingleton<VisualizationFactory>();
        services.AddSingleton<SpecCompiler>();
        services.AddSingleton<DashboardAiService>();

        // Live schema introspection (add more ISchemaIntrospector implementations for other DBs).
        services.AddSingleton<Introspection.ISchemaIntrospector, Introspection.SqliteSchemaIntrospector>();
        services.AddSingleton<Introspection.SchemaIntrospectionService>();

        services.AddHttpClient("RevealAI.Llm", c => c.Timeout = TimeSpan.FromSeconds(120));

        services.AddSingleton<ILlmClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("RevealAI.Llm");
            return options.Value.Provider switch
            {
                LlmProvider.OpenAI => new OpenAiClient(http, options),
                LlmProvider.Ollama => new OllamaClient(http, options),
                _ => new AnthropicClient(http, options)
            };
        });

        services.AddSingleton<IVisualizationRecommender>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            var llmUsable = options.Provider == LlmProvider.Ollama || !string.IsNullOrWhiteSpace(options.ApiKey);
            if (llmUsable)
                return new LlmRecommender(sp.GetRequiredService<ILlmClient>(), sp.GetRequiredService<ILogger<LlmRecommender>>());
            return new HeuristicRecommender();
        });

        return services;
    }
}
