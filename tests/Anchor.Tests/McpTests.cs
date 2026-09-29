using Anchor.Core;
using Anchor.Mcp;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

sealed class MemoryKeychain : IKeychain
{
    public Dictionary<string, string> Items { get; } = [];

    public bool Broken { get; set; }

    public Task<string?> GetAsync(string account) =>
        Broken ? throw new InvalidOperationException("no keychain") : Task.FromResult(Items.GetValueOrDefault(account));

    public Task SetAsync(string account, string secret)
    {
        if (Broken)
            throw new InvalidOperationException("no keychain");
        Items[account] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account)
    {
        Items.Remove(account);
        return Task.CompletedTask;
    }
}

public sealed class McpTests : IAsyncLifetime
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-mcp-").FullName;
    readonly List<AgentEvent> _events = [];
    FakeApprover _approver = new(Answer.Yes);
    Toolbox _toolbox = new([]);
    McpHub _hub = null!;

    static McpServer Fake(string name = "fake") => new(name, new McpServerConfig
    {
        Command = "python3",
        Args = [Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_mcp_server.py")],
    }, false);

    async Task StartAsync(Answer answer = Answer.Yes, bool yolo = false, params McpServer[] servers)
    {
        _approver = new FakeApprover(answer);
        var workspace = new Workspace(_root);
        _hub = new McpHub(_toolbox, new Gate(workspace, new Policy(workspace, yolo), _approver, _events.Add), _events.Add, new MemoryKeychain());
        await _hub.StartAsync(servers.Length > 0 ? servers : [Fake()], default);
    }

    Task<(string Text, bool Ok)> Call(string tool, object? args = null) =>
        _toolbox.InvokeAsync(new FunctionCallContent("c", tool, FakeChatClient.Args(args)), default);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ConnectsAndOffersNamespacedTools()
    {
        await StartAsync();

        Assert.Equal(new McpStatus("fake", "connected", 4), Assert.Single(_hub.Status));
        Assert.Equal(["mcp__fake__echo", "mcp__fake__fail", "mcp__fake__leak", "mcp__fake__write_note"], _toolbox.Names);
    }

    [Fact]
    public async Task ReadOnlyToolsRunWithoutAsking_OthersAsk()
    {
        await StartAsync();

        Assert.Equal(("echo: hi", true), await Call("mcp__fake__echo", new { text = "hi" }));
        Assert.Empty(_approver.Requests);

        Assert.Equal(("saved: n1", true), await Call("mcp__fake__write_note", new { note = "n1" }));
        var request = Assert.Single(_approver.Requests);
        Assert.Equal("Call mcp__fake__write_note", request.Title);
        Assert.Contains("\"note\":\"n1\"", request.Detail);
    }

    [Fact]
    public async Task Declined_DoesNotReachTheServer()
    {
        await StartAsync(Answer.No);

        var (text, ok) = await Call("mcp__fake__write_note", new { note = "n1" });

        Assert.False(ok);
        Assert.Contains("declined", text);
    }

    [Fact]
    public async Task Always_CoversLaterCallsToThatTool()
    {
        await StartAsync(Answer.Always);

        await Call("mcp__fake__write_note", new { note = "1" });
        await Call("mcp__fake__write_note", new { note = "2" });

        Assert.Single(_approver.Requests);
    }

    [Fact]
    public async Task Yolo_SkipsTheQuestion()
    {
        await StartAsync(yolo: true);

        Assert.True((await Call("mcp__fake__write_note", new { note = "1" })).Ok);
        Assert.Empty(_approver.Requests);
    }

    [Fact]
    public async Task OutputIsMaskedForSecrets()
    {
        Environment.SetEnvironmentVariable("ANCHOR_MCP_TEST_TOKEN", "tok-anchor-mcp-9f8e7d6c");
        try
        {
            await StartAsync();

            var (text, _) = await Call("mcp__fake__leak", new { name = "ANCHOR_MCP_TEST_TOKEN" });

            Assert.Equal(Secrets.Placeholder, text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANCHOR_MCP_TEST_TOKEN", null);
        }
    }

    [Fact]
    public async Task ServerErrors_AreToolFailures()
    {
        await StartAsync();

        Assert.Equal(("Error: this tool always fails", false), await Call("mcp__fake__fail"));
    }

    [Fact]
    public async Task ABrokenServer_IsReportedAndTheOthersStillConnect()
    {
        var broken = new McpServer("broken", new McpServerConfig { Command = "anchor-no-such-program" }, false);

        await StartAsync(servers: [broken, Fake()]);

        Assert.Equal("failed", _hub.Status.Single(s => s.Name == "broken").State);
        Assert.Equal("connected", _hub.Status.Single(s => s.Name == "fake").State);
        Assert.Contains(_events, e => e is Notice { Message: var m } && m.Contains("'broken' is unavailable"));
    }

    [Fact]
    public async Task Logout_DisconnectsAndRemovesTools_LoginReconnects()
    {
        await StartAsync();

        await _hub.LogoutAsync("fake");
        Assert.Empty(_toolbox.Names);
        Assert.Equal("signed out", _hub.Status.Single().State);

        await _hub.ConnectAsync("fake", default);
        Assert.Equal(4, _toolbox.Names.Count);
    }
}

public sealed class McpConfigTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-mcpcfg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Load_MergesProjectServers_UserConfigWinsAndInvalidOnesAreSkipped()
    {
        File.WriteAllText(Path.Combine(_root, ".mcp.json"), """
            { "mcpServers": {
                "docs": { "type": "http", "url": "https://docs.example/mcp" },
                "local": { "command": "node", "args": ["server.js"] },
                "broken": { "type": "http" }
            } }
            """);
        var user = new Dictionary<string, McpServerConfig> { ["local"] = new() { Command = "python3" } };

        var (servers, warnings) = McpConfig.Load(user, _root);

        Assert.Equal([("local", false, "python3"), ("docs", true, null)], servers.Select(s => (s.Name, s.FromProject, s.Config.Command)));
        Assert.True(servers[1].Config.IsHttp);
        Assert.Contains(warnings, w => w.Contains("'local' is ignored"));
        Assert.Contains(warnings, w => w.Contains("'broken' is skipped"));
    }

    [Fact]
    public void Expand_ReplacesEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("ANCHOR_EXPAND_TEST", "value");
        try
        {
            Assert.Equal("Bearer value/", McpConfig.Expand("Bearer ${ANCHOR_EXPAND_TEST}/${ANCHOR_UNSET_VAR_X}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANCHOR_EXPAND_TEST", null);
        }
    }

    [Fact]
    public void Trust_IsRememberedPerConfig_AndAskedAgainWhenTheConfigChanges()
    {
        var trust = new McpTrust(Path.Combine(_root, "trust.json"));
        var server = new McpServer("local", new McpServerConfig { Command = "node" }, true);

        Assert.Null(trust.Get("/work", server));
        trust.Set("/work", server, false);
        Assert.False(trust.Get("/work", server));
        Assert.Null(trust.Get("/other", server));
        Assert.Null(trust.Get("/work", server with { Config = new McpServerConfig { Command = "node", Args = ["evil.js"] } }));
    }
}

public class McpToolNameTests
{
    [Fact]
    public void Names_AreCleanedAndCappedAt64()
    {
        Assert.Equal("mcp__my_server__", McpTool.Prefix("my server"));
        Assert.Equal("mcp__s__a_b", McpTool.ToolName("s", "a.b"));
        var name = McpTool.ToolName("s", new string('t', 80));
        Assert.Equal(64, name.Length);
        Assert.StartsWith("mcp__s__ttt", name);
    }
}
