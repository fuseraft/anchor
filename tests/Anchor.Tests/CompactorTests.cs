using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

public class CompactorTests
{
    static readonly ChatOptions Options = new() { ModelId = "m" };

    static List<ChatMessage> Turns(int count, int size = 400)
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"request {i}"));
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}" })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{i}", new string('x', size))]));
            history.Add(new ChatMessage(ChatRole.Assistant, $"answer {i}"));
        }
        return history;
    }

    [Fact]
    public async Task SummarizesOlderTurnsAndKeepsRecentOnesVerbatim()
    {
        var history = Turns(6);
        var recent = history[^4..];
        var client = new FakeChatClient().Text("goal: things; files: f0-f3");

        var result = await new Compactor(1_000, preserveRatio: 0.3).CompactAsync(client, Options, history, default);

        Assert.Equal(CompactOutcome.Compacted, result.Outcome);
        Assert.Single(client.Requests);
        Assert.Equal(MessageKind.Summary, Messages.Kind(history[0]));
        Assert.Contains("files: f0-f3", history[0].Text);
        Assert.Equal(recent, history[^4..]);
        Assert.DoesNotContain(history, m => m.Text == "request 0");
        Assert.True(result.After < result.Before);
    }

    [Fact]
    public async Task TailNeverSplitsACallFromItsResult()
    {
        var history = Turns(5);

        await new Compactor(1_000, preserveRatio: 0.15).CompactAsync(new FakeChatClient().Text("s"), Options, history, default);

        Assert.True(Messages.IsUserInput(history[1]));
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId);
        var results = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId);
        Assert.Equal(calls, results);
    }

    [Fact]
    public async Task LastTurn_IsKeptEvenWhenOverBudget()
    {
        var history = Turns(4, size: 4_000);
        var current = history.FindLastIndex(Messages.IsUserInput);
        var kept = history[current..];

        await new Compactor(1_000, preserveRatio: 0.01).CompactAsync(new FakeChatClient().Text("s"), Options, history, default);

        Assert.Equal(kept, history[1..]);
    }

    [Fact]
    public async Task LargerSummary_IsRejectedAndHistoryUntouched()
    {
        var history = Turns(3, size: 10);
        var original = history.ToList();

        var result = await new Compactor(1_000, preserveRatio: 0).CompactAsync(new FakeChatClient().Text(new string('s', 5_000)), Options, history, default);

        Assert.Equal(CompactOutcome.Rejected, result.Outcome);
        Assert.Equal(original, history);
    }

    [Fact]
    public async Task EmptySummary_IsRejected()
    {
        var history = Turns(3);

        var result = await new Compactor(1_000, preserveRatio: 0).CompactAsync(new FakeChatClient().Text("   "), Options, history, default);

        Assert.Equal(CompactOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task OnlyRecentTurns_MeansNothingToCompactAndNoModelCall()
    {
        var history = Turns(2);
        var client = new FakeChatClient();

        var result = await new Compactor(100_000).CompactAsync(client, Options, history, default);

        Assert.Equal(CompactOutcome.NothingToCompact, result.Outcome);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SummarizerOverflow_RetriesWithShorterResults()
    {
        var history = Turns(4, size: 5_000);
        var client = new FakeChatClient()
            .Throws(new HttpRequestException("This model's maximum context length is 8192 tokens"))
            .Text("summary");

        var result = await new Compactor(1_000, preserveRatio: 0).CompactAsync(client, Options, history, default);

        Assert.Equal(CompactOutcome.Compacted, result.Outcome);
        Assert.True(client.Requests[1][1].Text.Length < client.Requests[0][1].Text.Length);
    }

    [Fact]
    public async Task RepeatedCompaction_FoldsThePreviousSummaryIn()
    {
        var history = Turns(4);
        var compactor = new Compactor(1_000, preserveRatio: 0);
        await compactor.CompactAsync(new FakeChatClient().Text("first summary"), Options, history, default);
        history.AddRange(Turns(2));
        var client = new FakeChatClient().Text("second summary");

        await compactor.CompactAsync(client, Options, history, default);

        Assert.Contains("EARLIER SUMMARY:\n[Earlier conversation, summarized]\n\nfirst summary", client.Requests[0][1].Text);
        Assert.Single(history, m => Messages.Kind(m) == MessageKind.Summary);
    }

    [Fact]
    public void Evict_OldestFirst_KeepsTheLatestRoundAndEveryPairing()
    {
        var history = Turns(3, size: 4_000);
        var latest = history[^2];

        var evicted = Compactor.TrimToolContent(history, targetTokens: 0);

        Assert.Equal(2, evicted);
        Assert.Same(latest, history[^2]);
        var first = Messages.ResultText(history[2].Contents.OfType<FunctionResultContent>().Single());
        Assert.StartsWith("[anchor: this result was removed to save context: read_file(f0).", first);
        Assert.Contains("Preview: \"xxxx", first);
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId);
        var results = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId);
        Assert.Equal(calls, results);
    }

    [Fact]
    public void Evict_StopsOnceUnderTarget()
    {
        var history = Turns(3, size: 4_000);
        var target = Messages.EstimateTokens(history) - 500;

        Assert.Equal(1, Compactor.TrimToolContent(history, target));
        Assert.Equal(4_000, Messages.ResultText(history[6].Contents.OfType<FunctionResultContent>().Single()).Length);
    }

    [Fact]
    public void Evict_StopsMidMessageWhenParallelCallsShareIt()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [.. Enumerable.Range(0, 3).Select(i => new FunctionCallContent($"p{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"p{i}" }))]),
            new(ChatRole.Tool, [.. Enumerable.Range(0, 3).Select(i => new FunctionResultContent($"p{i}", new string('y', 4_000)))]),
            new(ChatRole.Assistant, [new FunctionCallContent("last", "read_file", new Dictionary<string, object?> { ["path"] = "last" })]),
            new(ChatRole.Tool, [new FunctionResultContent("last", "small")]),
        ];

        var evicted = Compactor.TrimToolContent(history, Messages.EstimateTokens(history) - 500);

        Assert.Equal(1, evicted);
        Assert.Equal(2, history[2].Contents.OfType<FunctionResultContent>().Count(r => Messages.ResultText(r).Length == 4_000));
    }

    [Fact]
    public void Trim_ShrinksLargeOldCallArguments_KeepingTheRest()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "write it"),
            new(ChatRole.Assistant, [new FunctionCallContent("w", "write_file", new Dictionary<string, object?> { ["path"] = "big.cs", ["content"] = new string('c', 6_000) })]),
            new(ChatRole.Tool, [new FunctionResultContent("w", "Created big.cs")]),
            new(ChatRole.Assistant, [new FunctionCallContent("w2", "write_file", new Dictionary<string, object?> { ["path"] = "next.cs", ["content"] = new string('d', 6_000) })]),
            new(ChatRole.Tool, [new FunctionResultContent("w2", "Created next.cs")]),
        ];

        Assert.Equal(1, Compactor.TrimToolContent(history, targetTokens: 0));

        var old = history[1].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal(("w", "big.cs"), (old.CallId, (string)old.Arguments!["path"]!));
        Assert.Equal("[anchor: 6,000 characters omitted to save context]", old.Arguments["content"]);
        Assert.Equal(6_000, ((string)history[3].Contents.OfType<FunctionCallContent>().Single().Arguments!["content"]!).Length);
    }

    static List<ChatMessage> LongTurn(int rounds, Func<int, string>? note = null, int first = 0)
    {
        List<ChatMessage> history = [new(ChatRole.User, "go")];
        for (var i = first; i < first + rounds; i++)
        {
            AIContent call = new FunctionCallContent($"r{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"p{i}" });
            history.Add(new ChatMessage(ChatRole.Assistant, note?.Invoke(i) is { } text ? [new TextContent(text), call] : [call]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"r{i}", new string('z', 300))]));
        }
        return history;
    }

    static void AssertPaired(List<ChatMessage> history)
    {
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId);
        var results = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId);
        Assert.Equal(calls, results);
        for (var i = 0; i < history.Count; i++)
            if (history[i].Role == ChatRole.Tool)
                Assert.Equal(ChatRole.Assistant, history[i - 1].Role);
    }

    [Fact]
    public void DropRounds_RemovesOldestRoundsAndLeavesANote()
    {
        var history = LongTurn(30, i => i == 5 ? "checking p5 next" : null);
        var target = Messages.EstimateTokens(history) / 2;

        var dropped = Compactor.DropRounds(history, target);

        Assert.InRange(dropped, 10, 29);
        Assert.True(Messages.EstimateTokens(history) <= target + 100);
        Assert.Equal("go", history[0].Text);
        Assert.Equal(MessageKind.Note, Messages.Kind(history[1]));
        Assert.StartsWith("[anchor: earlier steps of this turn were removed", history[1].Text);
        Assert.Contains("- read_file: p0; p1; p2", history[1].Text);
        Assert.Contains("Your latest note before that: checking p5 next", history[1].Text);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
        Assert.Equal("r29", history[^1].Contents.OfType<FunctionResultContent>().Single().CallId);
        AssertPaired(history);
    }

    [Fact]
    public void DropRounds_Again_MergesIntoOneNote()
    {
        var history = LongTurn(30);
        Compactor.DropRounds(history, Messages.EstimateTokens(history) * 3 / 4);
        history.AddRange(LongTurn(10, first: 30)[1..]);

        Compactor.DropRounds(history, 0);

        Assert.Single(history, m => Messages.Kind(m) == MessageKind.Note);
        var lines = history[1].Text.Split('\n');
        Assert.StartsWith("- read_file: p0; p1;", lines[1]);
        Assert.Contains("more", lines[2]);
        Assert.Equal(4, history.Count);
        AssertPaired(history);
    }

    [Fact]
    public void DropRounds_NeverDropsTheLatestRound()
    {
        Assert.Equal(0, Compactor.DropRounds(LongTurn(1), 0));

        var history = LongTurn(5);
        Assert.Equal(4, Compactor.DropRounds(history, 0));
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public void Evict_LeavesSmallResultsAlone()
    {
        var history = Turns(3, size: 500);
        var original = history.ToList();

        Assert.Equal(0, Compactor.TrimToolContent(history, targetTokens: 0));
        Assert.Equal(original, history);
    }

    [Theory]
    [InlineData("This model's maximum context length is 131072 tokens", true)]
    [InlineData("prompt is too long: 210000 tokens > 200000 maximum", true)]
    [InlineData("Request too large; context_length_exceeded", true)]
    [InlineData("401 Unauthorized", false)]
    [InlineData("error at Foo.cs:line 413", false)]
    public void IsContextOverflow_ReadsProviderMessages(string message, bool expected) =>
        Assert.Equal(expected, Compactor.IsContextOverflow(new HttpRequestException(message)));

    [Fact]
    public void IsContextOverflow_RecognizesStatus413() =>
        Assert.True(Compactor.IsContextOverflow(new InvalidOperationException("wrapped", new HttpRequestException("x", null, System.Net.HttpStatusCode.RequestEntityTooLarge))));
}
