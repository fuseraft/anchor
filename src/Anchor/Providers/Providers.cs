using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Anchor.Providers;

/// <summary>How to reach one model. <c>Via</c> names the custom provider it came from, if any.</summary>
public sealed record ProviderSettings(string Provider, string Model, string? Endpoint, string? ApiKeyEnv,
    IReadOnlyDictionary<string, string>? Headers = null, long? ContextWindow = null, string? Via = null);

/// <summary>A named server from the config's <c>providers</c>, such as a LiteLLM proxy; models on it are written <c>name/model</c>.</summary>
public sealed class CustomProvider
{
    /// <summary>The wire protocol: <c>openai</c> (chat completions) or <c>anthropic</c> (messages).</summary>
    public string Type { get; set; } = "openai";

    public string? Endpoint { get; set; }

    public string? ApiKeyEnv { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public long? ContextWindow { get; set; }
}

public static class Providers
{
    static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);

    static readonly (string Prefix, ProviderSettings Preset)[] Presets =
    [
        ("claude-", new("anthropic", "", null, "ANTHROPIC_API_KEY")),
        ("grok-", new("openai", "", "https://api.x.ai/v1", "XAI_API_KEY")),
        ("gpt-", new("openai", "", null, "OPENAI_API_KEY")),
        ("o1", new("openai", "", null, "OPENAI_API_KEY")),
        ("o3", new("openai", "", null, "OPENAI_API_KEY")),
        ("o4", new("openai", "", null, "OPENAI_API_KEY")),
    ];

    static readonly (string Env, string Model)[] Fallbacks =
    [
        ("ANTHROPIC_API_KEY", "claude-sonnet-5"),
        ("XAI_API_KEY", "grok-4.5"),
    ];

    /// <summary>Fills in provider, endpoint and key variable from the model name, letting explicit config win.</summary>
    public static ProviderSettings Resolve(string? model, string? provider = null, string? endpoint = null, string? apiKeyEnv = null,
        IReadOnlyDictionary<string, CustomProvider>? custom = null)
    {
        model ??= Fallbacks.FirstOrDefault(f => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(f.Env))).Model
            ?? throw new InvalidOperationException(
                "No model configured. Pass --model, set provider.model in ~/.anchor/config.json, or set ANTHROPIC_API_KEY or XAI_API_KEY.");

        // Only the first slash separates the provider, since proxies use names like "work/anthropic/claude-sonnet-5".
        var slash = model.IndexOf('/');
        if (slash > 0 && custom is not null && custom.TryGetValue(model[..slash], out var named))
            return FromCustom(model[..slash], model[(slash + 1)..], named);

        var preset = Presets.FirstOrDefault(p => model.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Preset;
        if (preset is null && (endpoint is null || apiKeyEnv is null))
            throw new InvalidOperationException(
                $"Unknown model '{model}'. Add the server under providers in ~/.anchor/config.json and use <provider>/{model}, " +
                "or set provider.endpoint and provider.apiKeyEnv.");

        return new(
            provider ?? preset?.Provider ?? "openai",
            model,
            endpoint ?? preset?.Endpoint,
            apiKeyEnv ?? preset!.ApiKeyEnv);
    }

    static ProviderSettings FromCustom(string name, string model, CustomProvider p)
    {
        if (model.Length == 0)
            throw new InvalidOperationException($"No model after '{name}/'. Use {name}/<model>.");
        if (string.IsNullOrEmpty(p.Endpoint))
            throw new InvalidOperationException($"Provider '{name}' has no endpoint in ~/.anchor/config.json.");
        if (p.Type is not ("openai" or "anthropic"))
            throw new InvalidOperationException($"Provider '{name}' has type '{p.Type}'. Use 'openai' or 'anthropic'.");
        return new(p.Type, model, p.Endpoint, p.ApiKeyEnv, p.Headers, p.ContextWindow, name);
    }

    public static IChatClient Create(ProviderSettings settings)
    {
        // A custom provider without apiKeyEnv authenticates some other way (headers, or none at all), but the SDKs want a key.
        var key = settings.ApiKeyEnv is null ? "unused" : Environment.GetEnvironmentVariable(settings.ApiKeyEnv);
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"{settings.ApiKeyEnv} is not set (needed for {settings.Model}).");
        var headers = (settings.Headers ?? new Dictionary<string, string>()).ToDictionary(h => h.Key, h => Anchor.Mcp.McpConfig.Expand(h.Value));

        switch (settings.Provider)
        {
            case "anthropic":
                var http = new HttpClient { Timeout = NetworkTimeout };
                foreach (var (name, value) in headers)
                    http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
                var anthropic = new AnthropicClient(key, http);
                if (settings.Endpoint is not null)
                    anthropic.ApiUrlFormat = settings.Endpoint.TrimEnd('/') + "/{0}/{1}";
                return new AnthropicChatClient(anthropic);

            case "openai":
                var options = new OpenAIClientOptions { NetworkTimeout = NetworkTimeout, RetryPolicy = new ClientRetryPolicy(maxRetries: 2) };
                if (settings.Endpoint is not null)
                    options.Endpoint = new Uri(settings.Endpoint);
                if (headers.Count > 0)
                    options.AddPolicy(new HeadersPolicy(headers), PipelinePosition.PerCall);
                return new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(settings.Model).AsIChatClient();

            default:
                throw new InvalidOperationException($"Unknown provider '{settings.Provider}'. Use 'anthropic' or 'openai'.");
        }
    }

    /// <summary>Context window in tokens, from config or a conservative per-family default.</summary>
    /// <remarks>The family is found anywhere in the name, so proxy and Bedrock ids such as
    /// <c>anthropic.claude-sonnet-5</c> or <c>xai.grok-4.6</c> get the same default as the plain name.</remarks>
    public static long ContextWindow(ProviderSettings settings, long? configured) =>
        configured ?? settings.ContextWindow ?? settings.Model switch
        {
            var m when m.Contains("claude-", StringComparison.OrdinalIgnoreCase) => 200_000,
            var m when m.Contains("grok-4", StringComparison.OrdinalIgnoreCase) => 256_000,
            _ => 128_000,
        };

    /// <summary>Options every request carries; Anthropic requires an explicit output cap.</summary>
    public static ChatOptions Options(ProviderSettings settings) => new()
    {
        ModelId = settings.Model,
        MaxOutputTokens = settings.Provider == "anthropic" ? 16_000 : null,
    };
}

/// <summary>Adds configured headers to every OpenAI request.</summary>
sealed class HeadersPolicy(IReadOnlyDictionary<string, string> headers) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    void Apply(PipelineMessage message)
    {
        foreach (var (name, value) in headers)
            message.Request.Headers.Set(name, value);
    }
}
