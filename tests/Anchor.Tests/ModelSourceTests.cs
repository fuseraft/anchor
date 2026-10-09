using Anchor.Cli;

namespace Anchor.Tests;

public sealed class ModelSourceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anchor-keys-").FullName;
    readonly MemoryKeychain _keychain = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    CredentialsFile Credentials(string? contents = null)
    {
        var path = Path.Combine(_dir, "credentials");
        if (contents is not null)
            File.WriteAllText(path, contents);
        return new CredentialsFile(path);
    }

    [Fact]
    public async Task StoredKeys_RemembersAKeyFromTheKeychain()
    {
        _keychain.Items[Providers.Providers.KeychainAccount("TEST_KEY")] = "from-keychain";
        var keys = ModelSource.StoredKeys(_keychain, Credentials());

        Assert.Equal("from-keychain", await keys("TEST_KEY"));
        _keychain.Items.Clear();
        Assert.Equal("from-keychain", await keys("TEST_KEY"));
    }

    [Fact]
    public async Task StoredKeys_FallsBackToTheCredentialsFile_WithoutAKeychain()
    {
        _keychain.Broken = true;

        Assert.Equal("from-file", await ModelSource.StoredKeys(_keychain, Credentials("TEST_KEY=from-file\n"))("TEST_KEY"));
    }

    [Fact]
    public async Task StoredKeys_LooksAgainForAKeyItDidNotFind()
    {
        var keys = ModelSource.StoredKeys(_keychain, Credentials());
        Assert.Null(await keys("TEST_KEY"));

        _keychain.Items[Providers.Providers.KeychainAccount("TEST_KEY")] = "saved-since";
        Assert.Equal("saved-since", await keys("TEST_KEY"));
    }
}
