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

    // Background sub-agents report from several threads.
    void Record(AgentEvent e)
    {
        lock (_events)
            _events.Add(e);
    }

    SubAgentRunner Runner(IChatClient client) => new(_toolbox, "base prompt", Record, _ => Task.FromResult((client, new ChatOptions())));

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
    public void AgentTool_RejectsUnknownNames()
    {
        var tools = new AgentTools(Runner(new FakeChatClient()), [new AgentDefinition("reviewer", "reviews", null, null, "")], []);

        var e = Assert.Throws<ToolException>(() => tools.RunAgent("x", "nope"));

        Assert.Contains("Available: reviewer", e.Message);
    }

    [Fact]
    public void AgentTool_DescribesNamedAgents_AndSkillToolNeedsSkills()
    {
        var tools = new AgentTools(Runner(new FakeChatClient()), [new AgentDefinition("reviewer", "Reviews diffs", null, null, "")], []).All().ToList();

        Assert.Equal(["agent", "agent_status", "agent_stop"], tools.Select(t => t.Name));
        Assert.Contains("- reviewer: Reviews diffs", tools[0].Description);
    }

    [Fact]
    public async Task Background_TheTurnWaitsForTheReport_AndTheModelReadsItAsANote()
    {
        var release = new TaskCompletionSource();
        var sub = new FakeChatClient().Enqueue(ct => After(release.Task, "a.txt says alpha", ct));
        var main = new FakeChatClient()
            .Call("agent", new { task = "what does a.txt say?" })
            .Enqueue(_ =>
            {
                release.SetResult();
                return Reply("waiting for agent-1");
            })
            .Text("a.txt says alpha");
        var (agent, _) = MainAgent(main, sub);

        var end = await agent.RunTurnAsync("ask a sub-agent", default).WaitAsync(Wait);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.StartsWith("Started agent-1 in the background", ResultOf(agent, "agent"));
        var note = Assert.Single(main.Requests[^1], m => Messages.Kind(m) == MessageKind.Note);
        Assert.Equal("[anchor] Sub-agent agent-1 finished. Its report:\n\na.txt says alpha", note.Text);
    }

    [Fact]
    public async Task Background_WhileTheTurnWaits_TheUserCanAskHowItsGoing()
    {
        var release = new TaskCompletionSource();
        var waiting = new TaskCompletionSource();
        var reading = new TaskCompletionSource();
        var sub = new FakeChatClient().Call("read_file", new { path = "a.txt" }).Enqueue(ct =>
        {
            reading.SetResult();
            return After(release.Task, "done", ct);
        });
        var main = new FakeChatClient()
            .Call("agent", new { task = "look around" })
            .Enqueue(_ =>
            {
                waiting.SetResult();
                return Reply("waiting");
            })
            .Call("agent_status")
            .Enqueue(_ =>
            {
                release.SetResult();
                return Reply("agent-1 is still reading");
            })
            .Text("all done");
        var (agent, _) = MainAgent(main, sub);

        var turn = agent.RunTurnAsync("investigate", default);
        await Task.WhenAll(waiting.Task, reading.Task).WaitAsync(Wait);
        agent.Interject("how is it going?");
        Assert.Equal(TurnEnd.Completed, await turn.WaitAsync(Wait));

        Assert.Contains(main.Requests[2], m => m.Role == ChatRole.User && m.Text == "how is it going?");
        var status = ResultOf(agent, "agent_status");
        Assert.StartsWith("agent-1: running for", status);
        Assert.Contains("Latest: read_file a.txt", status);
        Assert.Contains(main.Requests[^1], m => Messages.Kind(m) == MessageKind.Note && m.Text.Contains("Sub-agent agent-1 finished"));
    }

    [Fact]
    public async Task Background_StoppingOne_DeliversNoReport()
    {
        var sub = new FakeChatClient().Enqueue(ct => After(new TaskCompletionSource().Task, "never", ct));
        var main = new FakeChatClient()
            .Call("agent", new { task = "dig" })
            .Call("agent_stop", new { id = "agent-1" })
            .Text("stopped it");
        var (agent, runner) = MainAgent(main, sub);

        Assert.Equal(TurnEnd.Completed, await agent.RunTurnAsync("go", default).WaitAsync(Wait));

        Assert.Equal("Stopped agent-1. Its report won't arrive.", ResultOf(agent, "agent_stop"));
        Assert.False(runner.Busy);
        Assert.DoesNotContain(agent.History, m => Messages.Kind(m) == MessageKind.Note);
    }

    [Fact]
    public async Task Background_CancellingTheTurn_StopsItsSubAgents()
    {
        using var cts = new CancellationTokenSource();
        var sub = new FakeChatClient().Enqueue(ct => After(new TaskCompletionSource().Task, "never", ct));
        var main = new FakeChatClient()
            .Call("agent", new { task = "dig" })
            .Enqueue(_ =>
            {
                cts.CancelAfter(50);
                return Reply("waiting");
            });
        var (agent, runner) = MainAgent(main, sub);

        Assert.Equal(TurnEnd.Cancelled, await agent.RunTurnAsync("go", cts.Token).WaitAsync(Wait));

        Assert.False(runner.Busy);
        Assert.Equal("No sub-agents have run in this turn.", runner.Status());
    }

    [Fact]
    public async Task Background_AtMostFourRunAtOnce()
    {
        var hang = new FakeChatClient();
        for (var i = 0; i < SubAgentRunner.MaxRunning; i++)
            hang.Enqueue(ct => After(new TaskCompletionSource().Task, "never", ct));
        var runner = Runner(hang);

        for (var i = 0; i < SubAgentRunner.MaxRunning; i++)
            runner.Start($"task {i}", null, default);
        var e = Assert.Throws<ToolException>(() => runner.Start("one more", null, default));

        Assert.Contains("4 sub-agents are already running", e.Message);
        await runner.StopAsync().WaitAsync(Wait);
        Assert.False(runner.Busy);
    }

    [Fact]
    public async Task Running_ShowsEachSubAgentsToolCallsAndTokens_AsItWorks()
    {
        var release = new TaskCompletionSource();
        var reading = new TaskCompletionSource();
        var sub = new FakeChatClient()
            .Enqueue(_ => new[]
            {
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", FakeChatClient.Args(new { path = "a.txt" }))]) { MessageId = "m" },
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new() { InputTokenCount = 1_200, OutputTokenCount = 34 })]) { MessageId = "m" },
            }.ToAsyncEnumerable())
            .Enqueue(ct =>
            {
                reading.SetResult();
                return After(release.Task, "done", ct);
            });
        var runner = Runner(sub);
        var changes = 0;
        runner.Changed += () => Interlocked.Increment(ref changes);

        runner.Start("look", null, default);
        await reading.Task.WaitAsync(Wait);

        Assert.Equal([new SubAgentStats("agent-1", 1, 1_234)], runner.Running);
        Assert.Contains("1 tool call, 1,234 tokens", runner.Status("agent-1"));
        Assert.True(changes >= 2);

        release.SetResult();
        await runner.StopAsync().WaitAsync(Wait);
        Assert.Empty(runner.Running);
    }

    [Fact]
    public void StatusLine_ListsRunningSubAgentsCompactly()
    {
        Assert.Equal("", Anchor.Cli.Repl.SubAgentStatus([]));
        Assert.Equal(" · agent-1: 7 calls, 12k tokens · reviewer-2: 1 call, 950 tokens · agent-3: 40 calls, 1.2M tokens · agent-4: 2 calls, 4.1k tokens",
            Anchor.Cli.Repl.SubAgentStatus([new("agent-1", 7, 12_345), new("reviewer-2", 1, 950), new("agent-3", 40, 1_234_567), new("agent-4", 2, 4_100)]));
    }

    [Fact]
    public async Task SerialApprover_NamesTheAskingSubAgent_AndAsksOneAtATime()
    {
        var inner = new HeldApprover();
        var approver = new SerialApprover(inner);

        var first = Task.Run(() =>
        {
            SerialApprover.ActFor("agent-1");
            return approver.ApproveAsync(new ApprovalRequest("Run: ls", null, null), default);
        });
        await inner.Asked.WaitAsync(Wait);
        var second = approver.ApproveAsync(new ApprovalRequest("Run: pwd", null, null), default);
        await Task.Delay(50);

        Assert.Equal(["[agent-1] Run: ls"], inner.Titles);
        inner.Answer.SetResult(Answer.Yes);
        Assert.Equal(Answer.Yes, await first.WaitAsync(Wait));
        Assert.Equal(Answer.Yes, await second.WaitAsync(Wait));
        Assert.Equal(["[agent-1] Run: ls", "Run: pwd"], inner.Titles);
    }

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    (Agent Agent, SubAgentRunner Runner) MainAgent(FakeChatClient main, FakeChatClient sub)
    {
        var runner = Runner(sub);
        _toolbox.Add(new AgentTools(runner, [], []).All());
        var agent = new Agent(main, _toolbox, "main prompt", Record) { Background = runner };
        runner.Report = agent.Notify;
        return (agent, runner);
    }

    static string ResultOf(Agent agent, string tool)
    {
        var callId = agent.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>().First(c => c.Name == tool).CallId;
        return Messages.ResultText(agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>().First(r => r.CallId == callId));
    }

    static async IAsyncEnumerable<ChatResponseUpdate> Reply(string text)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text) { MessageId = "m" };
    }

    static async IAsyncEnumerable<ChatResponseUpdate> After(Task release, string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await release.WaitAsync(ct);
        yield return new ChatResponseUpdate(ChatRole.Assistant, text) { MessageId = "m" };
    }

    /// <summary>Holds every approval until the test answers, and records what was asked.</summary>
    sealed class HeldApprover : IApprover
    {
        readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Titles { get; } = [];

        public Task Asked => _asked.Task;

        public TaskCompletionSource<Answer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (Titles)
                Titles.Add(request.Title);
            _asked.TrySetResult();
            return Answer.Task;
        }
    }

    [Fact]
    public void ListArguments_AreSummarizedByTheirCount()
    {
        static string Summary(string json) => Toolbox.Summarize(new FunctionCallContent("c", "tool",
            new Dictionary<string, object?> { ["tasks"] = System.Text.Json.JsonDocument.Parse(json).RootElement }));

        Assert.Equal("3 tasks", Summary("""["a", "b", "c"]"""));
        Assert.Equal("only one", Summary("""["only one"]"""));
    }
}
