using Anchor.Core;
using Anchor.Tools;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

public sealed class SubAgentTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-sub-").FullName;
    readonly List<AgentEvent> _events = [];
    readonly FakeApprover _approver = new(Answer.No);
    readonly Toolbox _toolbox;

    public SubAgentTests()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");
        var workspace = new Workspace(_root);
        var gate = new Gate(workspace, new Policy(workspace), _approver, _events.Add);
        _toolbox = new Toolbox([.. new FileTools(gate).All(), .. new EditTools(gate).All(), .. new ShellTool(gate).All()]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    SubAgentRunner Runner(FakeChatClient client) => new(_toolbox, "base prompt", _events.Add, _ => (client, new ChatOptions()));

    [Fact]
    public async Task RunsInAFreshContextAndReturnsOnlyTheReport()
    {
        var client = new FakeChatClient().Call("read_file", new { path = "a.txt" }).Text("a.txt says alpha");
        var runner = Runner(client);

        var report = await runner.RunAsync("what does a.txt say?", null, default);

        Assert.Equal("a.txt says alpha", report);
        Assert.Equal(["base prompt", "what does a.txt say?"], client.Requests[0].Select(m => m.Text.Split("\n\n# You are a sub-agent")[0]));
        Assert.Contains(_events, e => e is SubAgentEvent { Agent: "agent", Inner: ToolStarted { Name: "read_file" } });
        Assert.DoesNotContain(_events, e => e is ToolStarted);
    }

    [Fact]
    public async Task DefaultAgentCannotWrite()
    {
        var client = new FakeChatClient().Call("write_file", new { path = "b.txt", content = "x" }).Text("could not write");

        await Runner(client).RunAsync("write b.txt", null, default);

        Assert.Contains(_events, e => e is SubAgentEvent { Inner: ToolFinished { Ok: false, Result: var r } } && r.Contains("unknown tool 'write_file'"));
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task NamedAgentGetsItsToolsThroughTheSameGate()
    {
        var writer = new AgentDefinition("writer", "writes", ["write_file", "agent"], null, "You write files.");
        var client = new FakeChatClient().Call("write_file", new { path = "b.txt", content = "x" }).Text("done");

        await Runner(client).RunAsync("write b.txt", writer, default);

        Assert.Equal("Create b.txt", Assert.Single(_approver.Requests).Title);
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
        Assert.EndsWith("You write files.", client.Requests[0][0].Text);
    }

    [Fact]
    public async Task SubAgentsCannotSpawnSubAgents()
    {
        var writer = new AgentDefinition("writer", "writes", ["agent", "read_file"], null, "");
        _toolbox.Add(new AgentTools(Runner(new FakeChatClient()), [writer], []).All());
        var client = new FakeChatClient().Call("agent", new { task = "recurse" }).Text("done");

        await Runner(client).RunAsync("try", writer, default);

        Assert.Contains(_events, e => e is SubAgentEvent { Inner: ToolFinished { Ok: false, Result: var r } } && r.Contains("unknown tool 'agent'"));
    }

    [Fact]
    public async Task StoppedEarly_SaysSo()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 3; i++)
            client.Call("read_file", new { path = $"missing{i}.txt" });

        var report = await Runner(client).RunAsync("read", null, default);

        Assert.StartsWith("[sub-agent agent stopped early: 3 tool calls failed", report);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeChatClient().Enqueue(ct => { cts.Cancel(); return Hang(ct); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(client).RunAsync("x", null, cts.Token));

        static async IAsyncEnumerable<ChatResponseUpdate> Hang([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    [Fact]
    public async Task AgentTool_RejectsUnknownNames()
    {
        var tools = new AgentTools(Runner(new FakeChatClient()), [new AgentDefinition("reviewer", "reviews", null, null, "")], []);

        var e = await Assert.ThrowsAsync<ToolException>(() => tools.RunAgent("x", "nope"));

        Assert.Contains("Available: reviewer", e.Message);
    }

    [Fact]
    public void AgentTool_DescribesNamedAgents_AndSkillToolNeedsSkills()
    {
        var tools = new AgentTools(Runner(new FakeChatClient()), [new AgentDefinition("reviewer", "Reviews diffs", null, null, "")], []).All().ToList();

        Assert.Equal(["agent"], tools.Select(t => t.Name));
        Assert.Contains("- reviewer: Reviews diffs", tools[0].Description);
    }
}
