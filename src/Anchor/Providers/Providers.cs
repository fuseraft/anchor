using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
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

    /// <summary>True when an API key variable is set that picks a model with no config.</summary>
    public static bool HasDefaultModel() =>
        Fallbacks.Any(f => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(f.Env)));

    /// <summary>Fills in provider, endpoint and key variable from the model name, letting explicit config win.</summary>
    public static ProviderSettings Resolve(string? model, string? provider = null, string? endpoint = null, string? apiKeyEnv = null,
        IReadOnlyDictionary<string, CustomProvider>? custom = null)
    {
        model ??= Fallbacks.FirstOrDefault(f => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(f.Env))).Model
            ?? throw new InvalidOperationException(
                "No model is set up yet. Run anchor setup, or set ANTHROPIC_API_KEY or XAI_API_KEY, or pass --model.");

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

    /// <summary>The keychain account <c>anchor setup</c> stores a key under, named for the variable it stands in for.</summary>
    public static string KeychainAccount(string apiKeyEnv) => $"api-key/{apiKeyEnv}";

    /// <summary>Builds a client. The key comes from <c>ApiKeyEnv</c>, or from <paramref name="storedKey"/> (the keychain) when the variable is unset.
    /// Failed requests the provider says to retry are retried, and <paramref name="onRetry"/> hears about each one.</summary>
    public static IChatClient Create(ProviderSettings settings, Func<string, string?>? storedKey = null, Action<string>? onRetry = null)
    {
        // A custom provider without apiKeyEnv authenticates some other way (headers, or none at all), but the SDKs want a key.
        var key = settings.ApiKeyEnv is null ? "unused"
            : Environment.GetEnvironmentVariable(settings.ApiKeyEnv) is { Length: > 0 } fromEnv ? fromEnv
            : storedKey?.Invoke(settings.ApiKeyEnv);
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"{settings.ApiKeyEnv} is not set (needed for {settings.Model}). Set it, or run anchor setup to save a key.");
        var headers = (settings.Headers ?? new Dictionary<string, string>()).ToDictionary(h => h.Key, h => Anchor.Mcp.McpConfig.Expand(h.Value));
        var http = new HttpClient(new RetryHandler(onRetry)) { Timeout = NetworkTimeout };

        switch (settings.Provider)
        {
            case "anthropic":
                foreach (var (name, value) in headers)
                    http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
                // RetryHandler retries, so the SDK's own retries are off.
                var anthropic = new Anthropic.Core.ClientOptions { ApiKey = key, HttpClient = http, MaxRetries = 0, Timeout = NetworkTimeout };
                if (settings.Endpoint is not null)
                    anthropic.BaseUrl = settings.Endpoint.TrimEnd('/');
                return new AnthropicChatClient(new AnthropicClient(anthropic).AsIChatClient(settings.Model));

            case "openai":
                // RetryHandler retries, so the SDK's own policy doesn't multiply the attempts.
                var options = new OpenAIClientOptions
                {
                    NetworkTimeout = NetworkTimeout,
                    RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                    Transport = new HttpClientPipelineTransport(http),
                };
                if (settings.Endpoint is not null)
                    options.Endpoint = new Uri(settings.Endpoint);
                if (headers.Count > 0)
                    options.AddPolicy(new HeadersPolicy(headers), PipelinePosition.PerCall);
                return new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(settings.Model).AsIChatClient();

            default:
                throw new InvalidOperationException($"Unknown provider '{settings.Provider}'. Use 'anthropic' or 'openai'.");
        }
    }

    /// <summary>The model ids a server offers, from its <c>/models</c> list (OpenAI-compatible or Anthropic).</summary>
    public static async Task<List<string>> ListModelsAsync(HttpClient http, string type, string endpoint, string? key,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var url = type == "anthropic" ? endpoint.TrimEnd('/') + "/v1/models?limit=1000" : endpoint.TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (type == "anthropic")
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
            if (key is not null)
                request.Headers.Add("x-api-key", key);
        }
        else if (key is not null)
            request.Headers.Authorization = new("Bearer", key);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, Anchor.Mcp.McpConfig.Expand(value));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidOperationException($"{url} did not return a model list.");
        return [.. data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)];
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
