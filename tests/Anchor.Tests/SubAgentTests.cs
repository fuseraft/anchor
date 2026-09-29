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
    readonly Gate _gate;

    public SubAgentTests()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");
        var workspace = new Workspace(_root);
        _gate = new Gate(workspace, new Policy(workspace), _approver, Record);
        _toolbox = new Toolbox([.. new FileTools(_gate).All(), .. new EditTools(_gate).All(), .. new ShellTool(_gate).All()]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Parallel sub-agents report from several threads.
    void Record(AgentEvent e)
    {
        lock (_events)
            _events.Add(e);
    }

    SubAgentRunner Runner(IChatClient client) => new(_toolbox, "base prompt", Record, _ => (client, new ChatOptions()));

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

        Assert.Equal(["agent", "agents"], tools.Select(t => t.Name));
        Assert.Contains("- reviewer: Reviews diffs", tools[0].Description);
    }

    [Fact]
    public async Task Parallel_RunsTheTasksAtTheSameTime_AndReturnsEveryReportInOrder()
    {
        var report = await Runner(new RendezvousClient(3)).RunParallelAsync(["a", "b", "c"], default);

        Assert.Equal("## Task 1\nreport on a\n\n## Task 2\nreport on b\n\n## Task 3\nreport on c", report);
        Assert.Equal(["agent 1", "agent 2", "agent 3"], _events.OfType<SubAgentEvent>().Select(e => e.Agent).Distinct().Order());
    }

    [Fact]
    public async Task Parallel_RefusesWhatWouldAsk_ButTheCallerStillAsks()
    {
        var outside = Directory.CreateTempSubdirectory("anchor-sub-outside-").FullName;
        try
        {
            var file = Path.Combine(outside, "o.txt");
            File.WriteAllText(file, "outside");
            var client = new FakeChatClient().Call("read_file", new { path = file }).Text("could not read it");

            var report = await Runner(client).RunParallelAsync(["read o.txt"], default);

            Assert.Contains("could not read it", report);
            Assert.Empty(_approver.Requests);
            Assert.Contains(_events, e => e is SubAgentEvent { Inner: ToolFinished { Ok: false, Result: var r } } && r.Contains("Not allowed without approval"));

            await Assert.ThrowsAsync<ToolException>(() => _gate.ReadPathAsync(file, default));
            Assert.Single(_approver.Requests);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task AgentsTool_TakesOneToFourTasks()
    {
        var tools = new AgentTools(Runner(new FakeChatClient()), [], []);

        await Assert.ThrowsAsync<ToolException>(() => tools.RunAgents([]));
        await Assert.ThrowsAsync<ToolException>(() => tools.RunAgents(["1", "2", "3", "4", "5"]));
    }

    [Fact]
    public void AgentsCall_IsSummarizedByItsTaskCount()
    {
        static string Summary(string json) => Toolbox.Summarize(new FunctionCallContent("c", "agents",
            new Dictionary<string, object?> { ["tasks"] = System.Text.Json.JsonDocument.Parse(json).RootElement }));

        Assert.Equal("3 tasks", Summary("""["a", "b", "c"]"""));
        Assert.Equal("only one", Summary("""["only one"]"""));
    }

    /// <summary>Answers each request with its task, but only once every expected request is in flight at the same time.</summary>
    sealed class RendezvousClient(int expected) : IChatClient
    {
        readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _arrived;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var task = messages.Last().Text;
            if (Interlocked.Increment(ref _arrived) == expected)
                _all.SetResult();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"report on {task}") { MessageId = "m" };
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
