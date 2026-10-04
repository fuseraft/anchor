using Anchor.Mcp;
using Anchor.Providers;
using Microsoft.Extensions.AI;

namespace Anchor.Cli;

/// <summary>Turns a model name into a client for /model, /setup and sub-agents, reading the config fresh each time.
/// <paramref name="onRetry"/> hears when a client retries a failed request.</summary>
public sealed class ModelSource(Func<string, string?> storedKey, Func<Config> config, Action<string>? onRetry = null)
{
    public ProviderSettings Resolve(string model) => Providers.Providers.Resolve(model, custom: config().Providers);

    public IChatClient Create(ProviderSettings settings) => Providers.Providers.Create(settings, storedKey, onRetry);

    /// <summary>Keys saved by anchor setup, looked up at most once per variable; a missing keychain just means none.</summary>
    public static Func<string, string?> StoredKeys(IKeychain keychain)
    {
        var cache = new Dictionary<string, string?>();
        return env =>
        {
            lock (cache)
            {
                if (!cache.TryGetValue(env, out var key))
                {
                    try
                    {
                        key = keychain.GetAsync(Providers.Providers.KeychainAccount(env)).GetAwaiter().GetResult();
                    }
                    catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
                    {
                        key = null;
                    }
                    cache[env] = key;
                }
                return key;
            }
        };
    }
}
