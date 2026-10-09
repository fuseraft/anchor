using System.Collections.Concurrent;
using Anchor.Mcp;
using Anchor.Providers;
using Microsoft.Extensions.AI;

namespace Anchor.Cli;

/// <summary>Turns a model name into a client for /model, /setup and sub-agents, reading the config fresh each time.
/// <paramref name="onRetry"/> hears when a client retries a failed request.</summary>
public sealed class ModelSource(Func<string, Task<string?>> storedKey, Func<Config> config, Action<string>? onRetry = null)
{
    public ProviderSettings Resolve(string model) => Providers.Providers.Resolve(model, custom: config().Providers);

    /// <summary>A client for <paramref name="settings"/>, with the key saved by anchor setup when its variable isn't set.</summary>
    public async Task<IChatClient> CreateAsync(ProviderSettings settings)
    {
        var stored = settings.ApiKeyEnv is { } env && Environment.GetEnvironmentVariable(env) is not { Length: > 0 } ? await storedKey(env) : null;
        return Providers.Providers.Create(settings, stored, onRetry);
    }

    /// <summary>Keys saved by anchor setup, in the keychain or else the credentials file. A key found is remembered, so the
    /// keychain is asked once; a missing one is looked for again, since /setup may have just saved it.</summary>
    public static Func<string, Task<string?>> StoredKeys(IKeychain keychain, CredentialsFile credentials)
    {
        // Two lookups of one key at once may both ask the keychain; they find the same key, so that's harmless.
        var cache = new ConcurrentDictionary<string, string>();
        return async env =>
        {
            if (cache.TryGetValue(env, out var cached))
                return cached;
            string? key = null;
            try
            {
                key = await keychain.GetAsync(Providers.Providers.KeychainAccount(env));
            }
            catch (Exception e) when (e is KeychainException or OperationCanceledException)
            {
            }
            try
            {
                key ??= credentials.Get(env);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            if (key is not null)
                cache[env] = key;
            return key;
        };
    }
}
