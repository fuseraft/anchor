using Anchor.Mcp;

namespace Anchor.Tests;

/// <summary>
/// Keeps secrets in a dictionary instead of the OS keychain. 
/// <see cref="Broken"/> makes reads and writes throw, as on a machine with no keychain.
/// </summary>
sealed class MemoryKeychain : IKeychain
{
    public Dictionary<string, string> Items { get; } = [];

    public bool Broken { get; set; }

    public Task<string?> GetAsync(string account) =>
        Broken ? throw new KeychainException("no keychain") : Task.FromResult(Items.GetValueOrDefault(account));

    public Task SetAsync(string account, string secret)
    {
        if (Broken)
            throw new KeychainException("no keychain");
        Items[account] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account)
    {
        Items.Remove(account);
        return Task.CompletedTask;
    }
}
