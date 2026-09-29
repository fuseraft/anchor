using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

public enum CompactOutcome { Compacted, NothingToCompact, Rejected }

public sealed record Compaction(CompactOutcome Outcome, long Before, long After);

/// <summary>Replaces older turns with a summary, keeping recent turns verbatim.</summary>
public sealed class Compactor(long contextWindow, double triggerRatio = 0.8, double preserveRatio = 0.2)
{
    const string Instructions = """
        You are compacting a coding agent's conversation so it can continue without the original messages.
        Write a summary for the agent itself. Include, specifically:
        - The user's goals and requests (quote short ones verbatim) and any constraints they set.
        - Decisions made and why.
        - Files read, created or changed, and what state they are in now.
        - Commands run whose results still matter (builds, tests), and errors with how they were resolved.
        - What remains to do.
        Keep file paths, names, numbers and error messages exact. No pleasantries.
        """;

    static readonly int[] ResultCaps = [2_000, 800, 300, 100];

    const int MinEvictChars = 1_000;
    const int PreviewChars = 300;
    const double EvictTargetRatio = 0.6;

    public long ContextWindow { get; } = contextWindow;

    public long TriggerTokens => (long)(ContextWindow * triggerRatio);

    public bool ShouldCompact(long contextTokens) => contextTokens > TriggerTokens;

    public long EvictTarget => (long)(ContextWindow * EvictTargetRatio);

    /// <summary>
    /// Replaces large tool results and large call arguments (such as write_file content), oldest first, with short
    /// placeholders until the history is under <paramref name="targetTokens"/>. Works inside the current turn too;
    /// the latest round is never touched because the model hasn't seen its results yet.
    /// </summary>
    public static int TrimToolContent(List<ChatMessage> history, long targetTokens)
    {
        var latestRound = LatestRound(history);
        var calls = new Dictionary<string, FunctionCallContent>();
        foreach (var call in history.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            calls.TryAdd(call.CallId, call);

        var total = Messages.EstimateTokens(history);
        var trimmed = 0;
        for (var i = 0; i < latestRound && total > targetTokens; i++)
        {
            var contents = new List<AIContent>();
            foreach (var content in history[i].Contents)
                contents.Add(total > targetTokens ? Trim(content) : content);
            if (!contents.SequenceEqual(history[i].Contents))
                history[i] = new ChatMessage(history[i].Role, contents) { AdditionalProperties = history[i].AdditionalProperties };
        }
        return trimmed;

        AIContent Trim(AIContent content)
        {
            switch (content)
            {
                case FunctionResultContent r when Messages.ResultText(r) is { Length: > MinEvictChars } text:
                    var tombstone = Tombstone(calls.GetValueOrDefault(r.CallId), text);
                    total -= (text.Length - tombstone.Length) / 4;
                    trimmed++;
                    return new FunctionResultContent(r.CallId, tombstone);

                case FunctionCallContent f when f.Arguments?.Any(a => ArgText(a.Value) is { Length: > MinEvictChars }) == true:
                    var args = new Dictionary<string, object?>();
                    foreach (var (key, value) in f.Arguments)
                    {
                        if (ArgText(value) is { Length: > MinEvictChars } big && total > targetTokens)
                        {
                            args[key] = $"[anchor: {big.Length:N0} characters omitted to save context]";
                            total -= big.Length / 4;
                            trimmed++;
                        }
                        else
                            args[key] = value;
                    }
                    return new FunctionCallContent(f.CallId, f.Name, args);

                default:
                    return content;
            }
        }
    }

    /// <summary>
    /// Last resort for one very long turn: drops its oldest rounds, keeping each call with its result, and puts a note
    /// listing what they did right after the user's request. Returns the number of rounds dropped.
    /// </summary>
    public static int DropRounds(List<ChatMessage> history, long targetTokens)
    {
        var turn = history.FindLastIndex(Messages.IsUserInput);
        var latestRound = LatestRound(history);
        if (turn < 0)
            return 0;

        var from = turn + 1;
        var earlier = from < history.Count && Messages.Kind(history[from]) == MessageKind.Note ? history[from].Text : null;
        var first = earlier is null ? from : from + 1;
        if (latestRound <= first)
            return 0;

        // Stops only at an assistant message (a round start), so no call is separated from its result.
        var total = Messages.EstimateTokens(history);
        var to = first;
        while (to < latestRound)
        {
            total -= Messages.EstimateTokens([history[to]]);
            to++;
            if (history[to].Role == ChatRole.Assistant && total <= targetTokens)
                break;
        }

        var dropped = history[first..to];
        var rounds = dropped.Count(m => m.Role == ChatRole.Assistant);
        if (rounds == 0)
            return 0;
        history.RemoveRange(from, to - from);
        history.Insert(from, Messages.Create(MessageKind.Note, DropNote(earlier, dropped)));
        return rounds;
    }

    const string DropNoteHeader = "[anchor: earlier steps of this turn were removed to save context. What they did:]";

    static string DropNote(string? earlier, List<ChatMessage> dropped)
    {
        var lines = earlier?.Split('\n').Skip(1).Where(l => !l.StartsWith("Your latest note", StringComparison.Ordinal)).ToList() ?? [];
        foreach (var group in dropped.SelectMany(m => m.Contents).OfType<FunctionCallContent>().GroupBy(c => c.Name))
        {
            var items = group.Select(Toolbox.Summarize).Distinct().ToList();
            lines.Add($"- {group.Key}: {string.Join("; ", items.Take(20))}{(items.Count > 20 ? $"; +{items.Count - 20} more" : "")}");
        }
        var note = dropped.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Text.Length > 0)?.Text.Trim();
        if (note is { Length: > 0 })
            lines.Add($"Your latest note before that: {(note.Length > 500 ? note[..500] + "..." : note)}");
        return string.Join('\n', lines.Prepend(DropNoteHeader));
    }

    // The latest round is the last assistant message with tool calls, plus everything after it.
    static int LatestRound(List<ChatMessage> history) =>
        history.FindLastIndex(m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any()) is var i and >= 0 ? i : history.Count;

    static string? ArgText(object? value) => value switch
    {
        string s => s,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    static string Tombstone(FunctionCallContent? call, string text)
    {
        var what = call is null ? "an earlier tool call" : $"{call.Name}({Toolbox.Summarize(call)})";
        var preview = text[..PreviewChars].ReplaceLineEndings(" ");
        return $"[anchor: this result was removed to save context: {what}. Preview: \"{preview}...\". Run the tool again, with a narrower range, if you need it.]";
    }

    /// <summary>Compacts <paramref name="history"/> in place. The last turn (the one in progress, mid-turn) is always kept verbatim.</summary>
    public async Task<Compaction> CompactAsync(IChatClient client, ChatOptions options, List<ChatMessage> history, CancellationToken ct)
    {
        var before = Messages.EstimateTokens(history);
        var tailStart = TailStart(history);
        var older = history[..tailStart];
        if (!older.Any(Messages.IsUserInput))
            return new(CompactOutcome.NothingToCompact, before, before);

        var summary = await SummarizeAsync(client, options, older, ct);
        List<ChatMessage> candidate = [Messages.Create(MessageKind.Summary, $"[Earlier conversation, summarized]\n\n{summary}"), .. history[tailStart..]];
        var after = Messages.EstimateTokens(candidate);
        if (summary.Length == 0 || after >= before)
            return new(CompactOutcome.Rejected, before, before);

        history.Clear();
        history.AddRange(candidate);
        return new(CompactOutcome.Compacted, before, after);
    }

    // Keeps whole turns from the end, up to the preserve budget, and always the last turn.
    int TailStart(List<ChatMessage> history)
    {
        var starts = Enumerable.Range(0, history.Count).Where(i => Messages.IsUserInput(history[i])).ToList();
        if (starts.Count == 0)
            return history.Count;

        var tail = starts[^1];
        var budget = ContextWindow * preserveRatio;
        foreach (var start in starts.Where(s => s < tail).Reverse())
        {
            if (Messages.EstimateTokens(history[start..]) > budget)
                break;
            tail = start;
        }
        return tail;
    }

    static async Task<string> SummarizeAsync(IChatClient client, ChatOptions options, List<ChatMessage> older, CancellationToken ct)
    {
        var summaryOptions = new ChatOptions { ModelId = options.ModelId, MaxOutputTokens = options.MaxOutputTokens };
        ExceptionDispatchInfo? overflow = null;
        foreach (var cap in ResultCaps)
        {
            try
            {
                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, Messages.Transcript(older, cap))],
                    summaryOptions, ct);
                return response.Text.Trim();
            }
            catch (Exception e) when (IsContextOverflow(e) && !ct.IsCancellationRequested)
            {
                overflow = ExceptionDispatchInfo.Capture(e);
            }
        }
        overflow!.Throw();
        return "";
    }

    public static bool IsContextOverflow(Exception e)
    {
        string[] markers =
        [
            "context length", "context_length", "maximum context", "context window", "too many tokens",
            "prompt is too long", "input is too long", "reduce the length", "exceeds the maximum",
        ];
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestEntityTooLarge })
                return true;
            if (markers.Any(m => x.Message.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }
}
