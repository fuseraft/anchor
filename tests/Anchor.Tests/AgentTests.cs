using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

public class AgentTests
{
    readonly List<AgentEvent> _events = [];
    int _echoCalls;

    Agent NewAgent(FakeChatClient client, Limits? limits = null, params AIFunction[] extra) =>
        new(client, new Toolbox([Echo(), Fail(), .. extra]), "system", _events.Add, limits: limits);

    AIFunction Echo() => AIFunctionFactory.Create((string text) => { _echoCalls++; return "echo:" + text; }, "echo");

    static AIFunction Fail() => AIFunctionFactory.Create(string () => throw new ToolException("boom"), "fail");

    [Fact]
    public async Task TextTurn_RecordsHistoryAndStreamsText()
    {
        var agent = NewAgent(new FakeChatClient().Text("hello", input: 100, output: 7));

        var end = await agent.RunTurnAsync("hi", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Equal(["hi", "hello"], agent.History.Select(m => m.Text));
        Assert.Contains(_events, e => e is TextDelta { Text: "hello" });
        Assert.Contains(new UsageReport(100, 7, 0), _events);
    }

    [Fact]
    public async Task SystemPrompt_IsSentFirstButNotStoredInHistory()
    {
        var client = new FakeChatClient().Text("ok");
        var agent = NewAgent(client);

        await agent.RunTurnAsync("hi", CancellationToken.None);

        Assert.Equal(ChatRole.System, client.Requests[0][0].Role);
        Assert.DoesNotContain(agent.History, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task ToolCall_RunsAndResultIsSentBack()
    {
        var client = new FakeChatClient().Call("echo", new { text = "x" }, "c1").Text("done");
        var agent = NewAgent(client);

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        var result = client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("c1", result.CallId);
        Assert.Equal("echo:x", result.Result);
        Assert.Contains(_events, e => e is ToolStarted { Name: "echo", Summary: "x" });
        Assert.Contains(_events, e => e is ToolFinished { Name: "echo", Ok: true });
    }

    [Fact]
    public async Task UnknownTool_ReturnsErrorAndTurnContinues()
    {
        var client = new FakeChatClient().Call("nope").Text("sorry");
        var agent = NewAgent(client);

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Contains(_events, e => e is ToolFinished { Ok: false, Result: var r } && r.Contains("unknown tool"));
    }

    [Fact]
    public async Task IdenticalCalls_WarnAtThirdAndStopAtFifthWithoutRunningIt()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 6; i++)
            client.Call("echo", new { text = "same" });
        var agent = NewAgent(client);

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.LoopStopped, end);
        Assert.Equal(4, _echoCalls);
        var results = agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Contains("[anchor]", (string)results[2].Result!);
        Assert.DoesNotContain("[anchor]", (string)results[1].Result!);
        Assert.StartsWith("[anchor] Not run", (string)results[4].Result!);
        AssertEveryCallAnswered(agent);
    }

    [Fact]
    public async Task DifferentArguments_AreNotCountedAsRepeats()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 6; i++)
            client.Call("echo", new { text = i.ToString() });
        client.Text("done");
        var agent = NewAgent(client);

        Assert.Equal(TurnEnd.Completed, await agent.RunTurnAsync("go", CancellationToken.None));
        Assert.DoesNotContain(_events, e => e is LoopWarning);
    }

    [Fact]
    public async Task ConsecutiveFailures_StopTheTurn()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 3; i++)
            client.Call("fail", id: $"f{i}");
        var agent = NewAgent(client);

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.LoopStopped, end);
        Assert.Single(_events.OfType<LoopWarning>(), w => w.Message.Contains("2 tool calls"));
        Assert.Contains(_events, e => e is TurnEnded { Detail: var d } && d!.Contains("3 tool calls failed"));
    }

    [Fact]
    public async Task Stop_MidResponse_AnswersRemainingCallsWithoutRunningThem()
    {
        var client = new FakeChatClient().Calls(
            new FunctionCallContent("a", "fail"), new FunctionCallContent("b", "fail"),
            new FunctionCallContent("c", "fail"), new FunctionCallContent("d", "echo", FakeChatClient.Args(new { text = "x" })));
        var agent = NewAgent(client);

        Assert.Equal(TurnEnd.LoopStopped, await agent.RunTurnAsync("go", CancellationToken.None));
        Assert.Equal(0, _echoCalls);
        AssertEveryCallAnswered(agent);
    }

    [Fact]
    public async Task Cancel_DuringTool_AnswersOpenCallsAndKeepsHistoryValid()
    {
        using var cts = new CancellationTokenSource();
        var slow = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, "slow");
        var client = new FakeChatClient().Calls(new FunctionCallContent("s1", "slow"), new FunctionCallContent("s2", "slow"));
        var agent = NewAgent(client, extra: slow);

        var end = await agent.RunTurnAsync("go", cts.Token);

        Assert.Equal(TurnEnd.Cancelled, end);
        AssertEveryCallAnswered(agent);
        Assert.Contains(_events, e => e is TurnEnded { Reason: TurnEnd.Cancelled });
    }

    [Fact]
    public async Task Cancel_MidStream_KeepsPartialText()
    {
        using var cts = new CancellationTokenSource();
        var agent = new Agent(new FakeChatClient().TextThenHang("partial"), new Toolbox([]), "system",
            e => { _events.Add(e); if (e is TextDelta) cts.Cancel(); });

        var end = await agent.RunTurnAsync("go", cts.Token);

        Assert.Equal(TurnEnd.Cancelled, end);
        Assert.Equal(["go", "partial"], agent.History.Select(m => m.Text));
    }

    [Fact]
    public async Task ProviderError_BeforeAnyOutput_RollsBackTheTurn()
    {
        var agent = NewAgent(new FakeChatClient().Throws(new HttpRequestException("401 Unauthorized")));

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.Error, end);
        Assert.Empty(agent.History);
        Assert.Contains(_events, e => e is TurnEnded { Detail: var d } && d!.Contains("401"));
    }

    [Fact]
    public async Task ProviderError_AfterToolRound_KeepsCompletedWork()
    {
        var agent = NewAgent(new FakeChatClient().Call("echo", new { text = "x" }).Throws(new HttpRequestException("503")));

        Assert.Equal(TurnEnd.Error, await agent.RunTurnAsync("go", CancellationToken.None));
        Assert.Equal(3, agent.History.Count);
        AssertEveryCallAnswered(agent);
    }

    [Fact]
    public async Task Guard_ResetsBetweenTurns()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 2; i++)
            client.Call("echo", new { text = "same" });
        client.Text("one");
        for (var i = 0; i < 2; i++)
            client.Call("echo", new { text = "same" });
        client.Text("two");
        var agent = NewAgent(client);

        await agent.RunTurnAsync("a", CancellationToken.None);
        await agent.RunTurnAsync("b", CancellationToken.None);

        Assert.DoesNotContain(_events, e => e is LoopWarning);
    }

    static readonly string Long = new('a', 2_000);

    Agent CompactingAgent(FakeChatClient client, long window = 1_000) =>
        new(client, new Toolbox([Echo(), Fail()]), "system", _events.Add, compactor: new Compactor(window, preserveRatio: 0));

    [Fact]
    public async Task ContextNearlyFull_CompactsAfterTheTurn()
    {
        var client = new FakeChatClient().Text(Long, input: 100).Text("two", input: 850).Text("summary of one");
        var agent = CompactingAgent(client);

        await agent.RunTurnAsync("first", CancellationToken.None);
        Assert.DoesNotContain(_events, e => e is Compacted);

        await agent.RunTurnAsync("second", CancellationToken.None);

        Assert.Contains(_events, e => e is Compacted);
        Assert.Equal(["[Earlier conversation, summarized]\n\nsummary of one", "second", "two"], agent.History.Select(m => m.Text));
        Assert.Null(agent.LastContextTokens);
    }

    [Fact]
    public async Task ContextNearlyFull_MidTurn_KeepsTheCurrentTurn()
    {
        var client = new FakeChatClient().Text(Long, input: 100)
            .Enqueue(_ => new[]
            {
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("e1", "echo", FakeChatClient.Args(new { text = "x" }))]) { MessageId = "m" },
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new() { InputTokenCount = 900, OutputTokenCount = 5 })]) { MessageId = "m" },
            }.ToAsyncEnumerable())
            .Text("summary of one")
            .Text("finished", input: 200);
        var agent = CompactingAgent(client);

        await agent.RunTurnAsync("first", CancellationToken.None);
        var end = await agent.RunTurnAsync("second", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Equal(MessageKind.Summary, Messages.Kind(agent.History[0]));
        Assert.Equal("second", agent.History[1].Text);
        Assert.Equal("finished", agent.History[^1].Text);
        AssertEveryCallAnswered(agent);
    }

    [Fact]
    public async Task ContextOverflow_CompactsAndRetriesOnce()
    {
        var client = new FakeChatClient().Text(Long)
            .Throws(new HttpRequestException("prompt is too long: 300000 tokens > 200000 maximum"))
            .Text("summary of one")
            .Text("recovered");
        var agent = CompactingAgent(client, window: 100_000);

        await agent.RunTurnAsync("first", CancellationToken.None);
        var end = await agent.RunTurnAsync("second", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Contains(_events, e => e is Notice);
        Assert.Equal("recovered", agent.History[^1].Text);
    }

    static readonly AIFunction Big = AIFunctionFactory.Create((int n) => $"result{n}:" + new string('r', 4_000), "big");

    static IAsyncEnumerable<ChatResponseUpdate> BigCall(int n, long input) => new[]
    {
        new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent($"b{n}", "big", FakeChatClient.Args(new { n }))]) { MessageId = "m" },
        new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new() { InputTokenCount = input, OutputTokenCount = 5 })]) { MessageId = "m" },
    }.ToAsyncEnumerable();

    [Fact]
    public async Task OneLongTurn_TrimsItsOwnOldToolResults()
    {
        var client = new FakeChatClient().Enqueue(_ => BigCall(1, 300)).Enqueue(_ => BigCall(2, 1_700)).Text("done", input: 400);
        var agent = new Agent(client, new Toolbox([Big]), "system", _events.Add, compactor: new Compactor(2_000));

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Contains(_events, e => e is Trimmed { Items: 1 });
        Assert.DoesNotContain(_events, e => e is Compacted);
        var results = agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(Messages.ResultText).ToList();
        Assert.StartsWith("[anchor: this result was removed", results[0]);
        Assert.StartsWith("result2:", results[1]);
        Assert.Contains("[anchor: this result was removed", client.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(Messages.ResultText).First());
        AssertEveryCallAnswered(agent);
    }

    [Fact]
    public async Task Trimming_UsesTheRealTokenCount_NotJustTheEstimate()
    {
        var client = new FakeChatClient().Enqueue(_ => BigCall(1, 300)).Enqueue(_ => BigCall(2, 8_500)).Text("done", input: 400);
        var agent = new Agent(client, new Toolbox([Big]), "system", _events.Add, compactor: new Compactor(10_000));

        await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Contains(_events, e => e is Trimmed { Items: 1 });
        Assert.DoesNotContain(_events, e => e is Notice);
    }

    static readonly AIFunction Small = AIFunctionFactory.Create((int n) => $"small{n}:" + new string('s', 300), "small");

    static IAsyncEnumerable<ChatResponseUpdate> SmallCall(int n, long input) => new[]
    {
        new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent($"s{n}", "small", FakeChatClient.Args(new { n }))]) { MessageId = "m" },
        new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new() { InputTokenCount = input, OutputTokenCount = 5 })]) { MessageId = "m" },
    }.ToAsyncEnumerable();

    [Fact]
    public async Task ManySmallRounds_DropsTheOldestAndFinishes()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 40; i++)
        {
            var n = i;
            client.Enqueue(_ => SmallCall(n, 150 + 40L * n));
        }
        client.Text("done", input: 500);
        var agent = new Agent(client, new Toolbox([Small]), "system", _events.Add, compactor: new Compactor(2_000));

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Contains(_events, e => e is RoundsDropped);
        Assert.DoesNotContain(_events, e => e is Notice);
        Assert.Equal(MessageKind.Note, Messages.Kind(agent.History[1]));
        Assert.Contains("small: 0;", agent.History[1].Text);
        Assert.Contains(client.Requests[^1], m => Messages.Kind(m) == MessageKind.Note);
        AssertEveryCallAnswered(agent);
    }

    [Fact]
    public async Task TrimmingThatClearsTheTrigger_DoesNotAlsoDropRounds()
    {
        var client = new FakeChatClient().Enqueue(_ => BigCall(0, 100));
        for (var i = 1; i <= 10; i++)
        {
            var n = i;
            client.Enqueue(_ => SmallCall(n, 100));
        }
        client.Enqueue(_ => SmallCall(11, 8_300)).Text("done", input: 500);
        var agent = new Agent(client, new Toolbox([Big, Small]), "system", _events.Add, compactor: new Compactor(10_000));

        await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Contains(_events, e => e is Trimmed);
        Assert.DoesNotContain(_events, e => e is RoundsDropped);
    }

    [Fact]
    public async Task ContextOverflow_WithManySmallRounds_DropsAndRetries()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 6; i++)
        {
            var n = i;
            client.Enqueue(_ => SmallCall(n, 100));
        }
        client.Throws(new HttpRequestException("maximum context length exceeded")).Text("recovered");
        var agent = new Agent(client, new Toolbox([Small]), "system", _events.Add, compactor: new Compactor(100_000));

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Contains(_events, e => e is RoundsDropped);
        AssertEveryCallAnswered(agent);
    }

    [Fact]
    public async Task ContextOverflow_InOneLongTurn_TrimsAndRetries()
    {
        var client = new FakeChatClient()
            .Enqueue(_ => BigCall(1, 100)).Enqueue(_ => BigCall(2, 100))
            .Throws(new HttpRequestException("maximum context length exceeded"))
            .Text("recovered");
        var agent = new Agent(client, new Toolbox([Big]), "system", _events.Add, compactor: new Compactor(100_000));

        var end = await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, end);
        Assert.Contains(_events, e => e is Trimmed);
        Assert.Equal("recovered", agent.History[^1].Text);
    }

    [Fact]
    public async Task ContextOverflow_WithNothingToCompact_SurfacesTheError()
    {
        var client = new FakeChatClient().Throws(new HttpRequestException("maximum context length exceeded"));
        var agent = CompactingAgent(client, window: 100_000);

        var end = await agent.RunTurnAsync("huge", CancellationToken.None);

        Assert.Equal(TurnEnd.Error, end);
        Assert.Empty(agent.History);
    }

    static void AssertEveryCallAnswered(Agent agent)
    {
        var calls = agent.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId);
        var results = agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId);
        Assert.Equal(calls.Order(), results.Order());
    }

    [Fact]
    public async Task Interjection_IsReadAfterTheCurrentStepsToolResults()
    {
        Agent? agent = null;
        var typing = AIFunctionFactory.Create(() => { agent!.Interject("use tabs"); return "ok"; }, "slow");
        var client = new FakeChatClient().Call("slow", id: "c1").Text("done");
        agent = NewAgent(client, null, typing);

        await agent.RunTurnAsync("format it", CancellationToken.None);

        var sent = client.Requests[1];
        Assert.IsType<FunctionResultContent>(sent[^2].Contents.Single());
        Assert.Equal((ChatRole.User, "use tabs"), (sent[^1].Role, sent[^1].Text));
        Assert.Null(agent.TakeInterjections());
    }

    [Fact]
    public async Task Interjection_LeftUnreadWhenTheTurnEndsInText()
    {
        Agent? agent = null;
        var client = new FakeChatClient().Enqueue(_ =>
        {
            agent!.Interject("one");
            agent.Interject("two");
            return new[] { new ChatResponseUpdate(ChatRole.Assistant, "done") }.ToAsyncEnumerable();
        });
        agent = NewAgent(client);

        await agent.RunTurnAsync("go", CancellationToken.None);

        Assert.Equal(["go", "done"], agent.History.Select(m => m.Text));
        Assert.Equal("one\n\ntwo", agent.TakeInterjections());
    }

    /// <summary>Busy until the test finishes it. Finishing raises Changed and leaves no report, as a sub-agent does once
    /// its report has been read.</summary>
    sealed class HeldBackground : IBackground
    {
        volatile bool _busy = true;

        public bool Busy => _busy;

        public event Action? Changed;

        public void Finish()
        {
            _busy = false;
            Changed?.Invoke();
        }

        public Task StopAsync()
        {
            _busy = false;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Background_GoingIdleWithoutAReport_StillEndsTheTurn()
    {
        var background = new HeldBackground();
        var agent = NewAgent(new FakeChatClient().Text("done"));
        agent.Background = background;

        var turn = agent.RunTurnAsync("go", default);
        // Let the turn reach its wait for the background; finishing first would end it without waiting.
        await Task.Delay(200);
        Assert.False(turn.IsCompleted);
        background.Finish();

        Assert.Equal(TurnEnd.Completed, await turn.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Attachments_FollowTheMessageAsANote()
    {
        var client = new FakeChatClient().Text("ok");
        var agent = NewAgent(client);
        agent.Attachments = (text, _) => Task.FromResult<string?>(text.Contains('@') ? "contents" : null);

        await agent.RunTurnAsync("read @a.txt", CancellationToken.None);

        Assert.Equal(["read @a.txt", "contents", "ok"], agent.History.Select(m => m.Text));
        Assert.Equal(MessageKind.Note, Messages.Kind(agent.History[1]));
        Assert.Equal("contents", client.Requests[0][^1].Text);
    }

    [Fact]
    public async Task Attachments_GoWithAMessageTheModelNeverAnswered()
    {
        using var cts = new CancellationTokenSource();
        var agent = NewAgent(new FakeChatClient().Throws(new OperationCanceledException()));
        agent.Attachments = (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult<string?>("contents");
        };

        Assert.Equal(TurnEnd.Cancelled, await agent.RunTurnAsync("read @a.txt", cts.Token));
        Assert.Empty(agent.History);
    }

    [Fact]
    public async Task LastReply_IsTheLatestTextTheModelWrote()
    {
        var agent = new Agent(new FakeChatClient().Text("  first  ").Text(" "), new Toolbox([]), "system", _ => { });
        Assert.Null(agent.LastReply);

        await agent.RunTurnAsync("one", default);
        await agent.RunTurnAsync("two", default);

        Assert.Equal("first", agent.LastReply);
    }

    [Fact]
    public void Restore_AddNote_AndClear_ChangeTheHistoryBetweenTurns()
    {
        var agent = new Agent(new FakeChatClient(), new Toolbox([]), "system", _ => { });

        agent.Restore([new ChatMessage(ChatRole.User, "saved")]);
        agent.AddNote("[anchor] note");
        Assert.Equal(["saved", "[anchor] note"], agent.History.Select(m => m.Text));
        Assert.Equal(MessageKind.Note, Messages.Kind(agent.History[1]));

        agent.Clear();
        Assert.Empty(agent.History);
    }
}
