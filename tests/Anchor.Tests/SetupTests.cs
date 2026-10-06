using System.Net;
using System.Text.Json.Nodes;
using Anchor.Cli;
using Anchor.Providers;

namespace Anchor.Tests;

public class SetupTests : IDisposable
{
    readonly string _home = Directory.CreateTempSubdirectory("anchor-setup-").FullName;
    readonly MemoryKeychain _keychain = new();
    readonly FakeServer _server = new();

    string ConfigPath => Path.Combine(_home, "config.json");

    CredentialsFile Credentials => new(Path.Combine(_home, "credentials"));

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task CustomServer_SavesTheProviderTheKeyAndTheChosenModel()
    {
        _server.Models["https://litellm.corp.example/v1/models"] = ["anthropic.claude-sonnet-5", "xai.grok-4.6"];
        var io = new ScriptedIO("4", "https://litellm.corp.example/v1", "", "ANCHOR_TEST_SETUP_KEY", "sk-work", "1");

        var model = await Run(io);

        Assert.Equal("litellm/anthropic.claude-sonnet-5", model);
        var config = ReadConfig();
        Assert.Equal("litellm/anthropic.claude-sonnet-5", (string?)config["provider"]!["model"]);
        Assert.Equal("https://litellm.corp.example/v1", (string?)config["providers"]!["litellm"]!["endpoint"]);
        Assert.Equal("ANCHOR_TEST_SETUP_KEY", (string?)config["providers"]!["litellm"]!["apiKeyEnv"]);
        Assert.Equal("sk-work", _keychain.Items[Providers.Providers.KeychainAccount("ANCHOR_TEST_SETUP_KEY")]);
        Assert.Equal("Bearer sk-work", _server.Authorization);
        Assert.Contains(io.Output, l => l.Contains("xai.grok-4.6"));
    }

    [Fact]
    public async Task CustomServer_AddsV1WhenTheBareUrlHasNoModelList()
    {
        _server.Models["http://localhost:4000/v1/models"] = ["gpt-4.1"];
        var io = new ScriptedIO("4", "http://localhost:4000", "", "none", "gpt-4.1");

        var model = await Run(io);

        Assert.Equal("local/gpt-4.1", model);
        var saved = ReadConfig()["providers"]!["local"]!;
        Assert.Equal("http://localhost:4000/v1", (string?)saved["endpoint"]);
        Assert.Null(saved["apiKeyEnv"]);
    }

    [Fact]
    public async Task CustomServer_WithoutAModelList_AcceptsATypedName()
    {
        var io = new ScriptedIO("4", "https://proxy.example/v1", "work", "none", "anthropic.claude-sonnet-5");

        Assert.Equal("work/anthropic.claude-sonnet-5", await Run(io));
        Assert.Contains(io.Output, l => l.Contains("Couldn't list models"));
    }

    [Fact]
    public async Task KeepsOtherSettings_AndBacksUpACommentedConfig()
    {
        File.WriteAllText(ConfigPath, """
            {
              // mine
              "provider": { "model": "old", "endpoint": "http://old", "contextWindow": 9 },
              "providers": { "work": { "endpoint": "http://old/v1", "headers": { "X-Team": "a" } } },
              "mcpServers": { "files": { "command": "npx" } },
            }
            """);
        _server.Models["https://new.example/v1/models"] = ["m1"];
        var io = new ScriptedIO("4", "https://new.example/v1", "work", "none", "");

        await Run(io);

        var config = ReadConfig();
        Assert.Equal("""{"model":"work/m1"}""", config["provider"]!.ToJsonString());
        Assert.Equal("https://new.example/v1", (string?)config["providers"]!["work"]!["endpoint"]);
        Assert.Equal("a", (string?)config["providers"]!["work"]!["headers"]!["X-Team"]);
        Assert.Equal("npx", (string?)config["mcpServers"]!["files"]!["command"]);
        Assert.Contains("// mine", File.ReadAllText(ConfigPath + ".bak"));
        Assert.Equal("a", _server.Headers["X-Team"]);
    }

    [Fact]
    public async Task BuiltIn_UsesTheEnvironmentKey_AndOffersOnlyThatProvidersModels()
    {
        var before = Environment.GetEnvironmentVariable("XAI_API_KEY");
        Environment.SetEnvironmentVariable("XAI_API_KEY", "xai-test");
        try
        {
            _server.Models["https://api.x.ai/v1/models"] = ["grok-4.5", "grok-imagine", "text-embedding"];
            var io = new ScriptedIO("3", "");

            Assert.Equal("grok-4.5", await Run(io));
            Assert.Equal("""{"model":"grok-4.5"}""", ReadConfig()["provider"]!.ToJsonString());
            Assert.DoesNotContain(io.Output, l => l.Contains("text-embedding"));
            Assert.Empty(_keychain.Items);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", before);
        }
    }

    [Fact]
    public async Task EndOfInput_StopsWithoutWriting()
    {
        Assert.Null(await Run(new ScriptedIO("4")));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task BrokenKeychain_SavesTheKeyInAFileOnlyTheUserCanRead()
    {
        _keychain.Broken = true;
        var io = new ScriptedIO("4", "https://proxy.example/v1", "work", "ANCHOR_TEST_SETUP_KEY2", "sk-file", "m");

        Assert.Equal("work/m", await Run(io));
        Assert.Equal("sk-file", Credentials.Get("ANCHOR_TEST_SETUP_KEY2"));
        Assert.Contains(io.Output, l => l.Contains("Saved the key in") && l.Contains(Renderer.ShortPath(Credentials.Path)));
        Assert.Contains(io.Output, l => l.Contains("no OS keychain"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Credentials.Path));
    }

    [Fact]
    public async Task RejectedKey_IsAskedForAgain_AndNeverSaved()
    {
        _server.Models["https://api.anthropic.com/v1/models"] = ["claude-sonnet-5", "claude-opus-5-5"];
        _server.RejectedKeys.Add("sk-typo");
        var io = new ScriptedIO("1", "sk-typo", "sk-good", "");

        Assert.Equal("claude-sonnet-5", await Run(io));
        Assert.Contains("  You can create a key at https://platform.claude.com/settings/keys", io.Output);
        Assert.Contains("  ✗ That key was rejected (401 Unauthorized).", io.Output);
        Assert.Equal("sk-good", Assert.Single(_keychain.Items).Value);
    }

    [Fact]
    public async Task RejectedKey_ThenBlank_StopsWithoutWriting()
    {
        _server.RejectedKeys.Add("sk-typo");
        var io = new ScriptedIO("1", "sk-typo", "");

        Assert.Null(await Run(io));
        Assert.False(File.Exists(ConfigPath));
        Assert.Empty(_keychain.Items);
        Assert.Contains(io.Output, l => l.Contains("No key entered"));
    }

    [Fact]
    public async Task RejectedEnvironmentKey_SaysToFixTheVariable()
    {
        var before = Environment.GetEnvironmentVariable("XAI_API_KEY");
        Environment.SetEnvironmentVariable("XAI_API_KEY", "xai-revoked");
        try
        {
            _server.RejectedKeys.Add("xai-revoked");

            var io = new ScriptedIO("3");

            Assert.Null(await Run(io));
            Assert.Contains(io.Output, l => l.Contains("The key in $XAI_API_KEY was rejected"));
            Assert.False(File.Exists(ConfigPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", before);
        }
    }

    [Fact]
    public async Task KeyInTheCredentialsFile_IsOfferedToKeep()
    {
        Credentials.Set("ANTHROPIC_API_KEY", "sk-saved");
        _server.Models["https://api.anthropic.com/v1/models"] = ["claude-sonnet-5"];
        var io = new ScriptedIO("1", "1", "");

        Assert.Equal("claude-sonnet-5", await Run(io));
        Assert.Contains(io.Output, l => l.Contains("A key for ANTHROPIC_API_KEY is already saved"));
        Assert.Equal("sk-saved", _server.ApiKey);
    }

    [Fact]
    public void CredentialsFile_ReplacesAKeyAndKeepsTheOthers()
    {
        Credentials.Set("A_KEY", "one");
        Credentials.Set("B_KEY", "two");
        Credentials.Set("A_KEY", "three");

        Assert.Equal("three", Credentials.Get("A_KEY"));
        Assert.Equal("two", Credentials.Get("B_KEY"));
        Assert.Null(Credentials.Get("C_KEY"));
        Assert.Throws<ArgumentException>(() => Credentials.Set("A_KEY", "x\nB_KEY=evil"));
    }

    [Fact]
    public void Create_FallsBackToTheStoredKey()
    {
        var settings = new ProviderSettings("openai", "gpt-4.1", null, "ANCHOR_TEST_UNSET_KEY");

        Providers.Providers.Create(settings, env => env == "ANCHOR_TEST_UNSET_KEY" ? "stored" : null);
        var e = Assert.Throws<InvalidOperationException>(() => Providers.Providers.Create(settings, _ => null));
        Assert.Contains("anchor setup", e.Message);
    }

    [Fact]
    public void Notes_AreIndented_AndLongOnesLineUpUnderTheirText()
    {
        var io = new ScriptedIO();
        ISetupIO setup = io;

        setup.Note("Saved.\nSecond line.", ok: true);
        setup.Note("Failed.", ok: false);
        setup.Note("Plain.\nMore.");

        Assert.Equal(["  ✓ Saved.", "    Second line.", "  ✗ Failed.", "  Plain.", "  More."], io.Output);
    }

    [Theory]
    [InlineData("/srv/work/project", 100, "/srv/work/project")]
    [InlineData("/srv/a-very-long-folder-name/another-long-one/project", 30, "…/another-long-one/project")]
    [InlineData("/srv/a-very-long-folder-name/another-long-one/project", 12, "…/project")]
    public void ShortPath_KeepsTheTrailingFoldersThatFit(string path, int max, string expected) =>
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), Renderer.ShortPath(path.Replace('/', Path.DirectorySeparatorChar), max));

    [Fact]
    public void ShortPath_WritesTheHomeDirectoryAsTilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine("~", "code", "app"), Renderer.ShortPath(Path.Combine(home, "code", "app")));
    }

    [Fact]
    public void ParsesTheSetupCommand()
    {
        Assert.True(Options.Parse(["setup"], TextWriter.Null, TextWriter.Null).Options!.Setup);
    }

    Task<string?> Run(ScriptedIO io) => new Setup(io, _keychain, Credentials, new HttpClient(_server), ConfigPath).RunAsync();

    JsonNode ReadConfig() => JsonNode.Parse(File.ReadAllText(ConfigPath))!;

    sealed class ScriptedIO(params string[] answers) : ISetupIO
    {
        readonly Queue<string> _answers = new(answers);

        public List<string> Output { get; } = [];

        public string? Ask(string prompt)
        {
            Output.Add(prompt);
            return _answers.TryDequeue(out var a) ? a : null;
        }

        public string? AskSecret(string prompt) => Ask(prompt);

        // "" takes the default, a number picks by position, anything else is the answer itself.
        public string? Select(string title, IReadOnlyList<string> choices, string? selected = null, bool allowTyped = false)
        {
            Output.Add(title);
            Output.AddRange(choices);
            if (!_answers.TryDequeue(out var a))
                return null;
            return a.Length == 0 ? selected ?? choices[0]
                : int.TryParse(a, out var n) ? choices[n - 1]
                : a;
        }

        public void Line(string text = "") => Output.Add(text);
    }

    sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, string[]> Models { get; } = [];

        public string? Authorization { get; private set; }

        public string? ApiKey { get; private set; }

        /// <summary>Keys answered with 401, in either the Bearer or the x-api-key header.</summary>
        public HashSet<string> RejectedKeys { get; } = [];

        public Dictionary<string, string> Headers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.TryGetValues("x-api-key", out var k) ? k.First() : null;
            if (RejectedKeys.Contains(request.Headers.Authorization?.Parameter ?? "") || RejectedKeys.Contains(ApiKey ?? ""))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            foreach (var h in request.Headers)
                Headers[h.Key] = string.Join(",", h.Value);
            var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (!Models.TryGetValue(url, out var ids))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = new JsonObject { ["data"] = new JsonArray([.. ids.Select(id => (JsonNode)new JsonObject { ["id"] = id })]) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) });
        }
    }
}
