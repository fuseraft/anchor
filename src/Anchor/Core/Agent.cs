using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>Runs turns: stream a response, execute its tool calls through the gate, repeat until the model answers in text.</summary>
public sealed class Agent(IChatClient client, Toolbox toolbox, string systemPrompt, Action<AgentEvent> emit, ChatOptions? options = null, Limits? limits = null)
{
    IChatClient _client = client;
    ChatOptions _options = WithTools(options, toolbox);
    readonly Limits _limits = limits ?? new Limits();

    public List<ChatMessage> History { get; } = [];

    public string SystemPrompt { get; set; } = systemPrompt;

    /// <summary>Switches model/provider; history carries over.</summary>
    public void Use(IChatClient newClient, ChatOptions newOptions)
    {
        _client = newClient;
        _options = WithTools(newOptions, toolbox);
    }

    public async Task<TurnEnd> RunTurnAsync(string input, CancellationToken ct)
    {
        var turnStart = History.Count;
        History.Add(new ChatMessage(ChatRole.User, input));

        var guard = new LoopGuard(_limits);
        var usage = new UsageDetails();
        List<ChatResponseUpdate> streamed = [];
        List<AIContent>? results = null;

        try
        {
            while (true)
            {
                streamed = [];
                await foreach (var update in _client.GetStreamingResponseAsync(Context(), _options, ct))
                {
                    streamed.Add(update);
                    foreach (var content in update.Contents)
                        if (content is TextContent { Text.Length: > 0 } text)
                            emit(new TextDelta(text.Text));
                }

                var response = streamed.ToChatResponse();
                streamed = [];
                if (response.Usage is { } u)
                    usage.Add(u);
                History.AddRange(response.Messages);

                var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                if (calls.Count == 0)
                    return End(TurnEnd.Completed);

                results = [];
                string? stop = null;
                foreach (var call in calls)
                {
                    if (stop is not null)
                    {
                        results.Add(new FunctionResultContent(call.CallId, "[anchor] Not run: the turn was stopped."));
                        continue;
                    }

                    var before = guard.BeforeCall(call);
                    if (before.Action == GuardAction.Stop)
                    {
                        stop = before.Message;
                        results.Add(new FunctionResultContent(call.CallId, $"[anchor] Not run: {before.Message}"));
                        continue;
                    }

                    emit(new ToolStarted(call.CallId, call.Name, Toolbox.Summarize(call)));
                    var (text, ok) = await toolbox.InvokeAsync(call, ct);
                    emit(new ToolFinished(call.CallId, call.Name, ok, text));

                    var after = guard.AfterCall(ok);
                    foreach (var verdict in new[] { before, after })
                    {
                        if (verdict.Action == GuardAction.None)
                            continue;
                        emit(new LoopWarning(verdict.Message));
                        if (verdict.Action == GuardAction.Warn)
                            text += $"\n\n[anchor] {verdict.Message}";
                        else
                            stop = verdict.Message;
                    }
                    results.Add(new FunctionResultContent(call.CallId, text));
                }

                History.Add(new ChatMessage(ChatRole.Tool, results));
                results = null;
                if (stop is not null)
                    return End(TurnEnd.LoopStopped, stop);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Recover(turnStart, streamed, results, "Cancelled by the user.");
            return End(TurnEnd.Cancelled);
        }
        catch (Exception e)
        {
            Recover(turnStart, streamed, results, "Not run: the turn failed.");
            return End(TurnEnd.Error, e.Message);
        }

        TurnEnd End(TurnEnd reason, string? detail = null)
        {
            emit(new UsageReport(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, CachedInput(usage)));
            emit(new TurnEnded(reason, detail));
            return reason;
        }
    }

    IEnumerable<ChatMessage> Context() => [new ChatMessage(ChatRole.System, SystemPrompt), .. History];

    // Leaves history valid after an interrupted turn: keeps streamed text, answers every open call,
    // and drops the turn entirely if the model never produced anything.
    void Recover(int turnStart, List<ChatResponseUpdate> streamed, List<AIContent>? results, string reason)
    {
        var partial = streamed.ToChatResponse().Text;
        if (partial.Length > 0)
            History.Add(new ChatMessage(ChatRole.Assistant, partial));

        if (History[^1] is { Role: var role } last && role == ChatRole.Assistant)
        {
            var answered = (results ?? []).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
            var open = last.Contents.OfType<FunctionCallContent>().Where(c => !answered.Contains(c.CallId)).ToList();
            if (open.Count > 0)
                History.Add(new ChatMessage(ChatRole.Tool,
                    [.. results ?? [], .. open.Select(c => new FunctionResultContent(c.CallId, $"[anchor] {reason}"))]));
        }

        if (History.Count == turnStart + 1)
            History.RemoveAt(turnStart);
    }

    static long CachedInput(UsageDetails usage) =>
        usage.CachedInputTokenCount
        ?? (usage.AdditionalCounts?.TryGetValue("CacheReadInputTokens", out var n) == true ? n : 0);

    static ChatOptions WithTools(ChatOptions? options, Toolbox toolbox)
    {
        var o = options?.Clone() ?? new ChatOptions();
        o.Tools = toolbox.Declarations;
        return o;
    }
}
