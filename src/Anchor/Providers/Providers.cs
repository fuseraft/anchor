using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Anchor.Providers;

public sealed record ProviderSettings(string Provider, string Model, string? Endpoint, string ApiKeyEnv);

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
    public static ProviderSettings Resolve(string? model, string? provider = null, string? endpoint = null, string? apiKeyEnv = null)
    {
        model ??= Fallbacks.FirstOrDefault(f => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(f.Env))).Model
            ?? throw new InvalidOperationException(
                "No model configured. Pass --model, set provider.model in ~/.anchor/config.json, or set ANTHROPIC_API_KEY or XAI_API_KEY.");

        var preset = Presets.FirstOrDefault(p => model.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Preset;
        if (preset is null && (endpoint is null || apiKeyEnv is null))
            throw new InvalidOperationException(
                $"Unknown model '{model}'. Set provider.endpoint and provider.apiKeyEnv in ~/.anchor/config.json for OpenAI-compatible servers.");

        return new(
            provider ?? preset?.Provider ?? "openai",
            model,
            endpoint ?? preset?.Endpoint,
            apiKeyEnv ?? preset!.ApiKeyEnv);
    }

    public static IChatClient Create(ProviderSettings settings)
    {
        var key = Environment.GetEnvironmentVariable(settings.ApiKeyEnv);
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"{settings.ApiKeyEnv} is not set (needed for {settings.Model}).");

        switch (settings.Provider)
        {
            case "anthropic":
                var anthropic = new AnthropicClient(key, new HttpClient { Timeout = NetworkTimeout });
                if (settings.Endpoint is not null)
                    anthropic.ApiUrlFormat = settings.Endpoint.TrimEnd('/') + "/{0}/{1}";
                return new AnthropicChatClient(anthropic);

            case "openai":
                var options = new OpenAIClientOptions { NetworkTimeout = NetworkTimeout, RetryPolicy = new ClientRetryPolicy(maxRetries: 2) };
                if (settings.Endpoint is not null)
                    options.Endpoint = new Uri(settings.Endpoint);
                return new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(settings.Model).AsIChatClient();

            default:
                throw new InvalidOperationException($"Unknown provider '{settings.Provider}'. Use 'anthropic' or 'openai'.");
        }
    }

    /// <summary>Options every request carries; Anthropic requires an explicit output cap.</summary>
    public static ChatOptions Options(ProviderSettings settings) => new()
    {
        ModelId = settings.Model,
        MaxOutputTokens = settings.Provider == "anthropic" ? 16_000 : null,
    };
}
