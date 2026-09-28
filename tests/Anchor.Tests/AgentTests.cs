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

    static void AssertEveryCallAnswered(Agent agent)
    {
        var calls = agent.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId);
        var results = agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId);
        Assert.Equal(calls.Order(), results.Order());
    }
}
