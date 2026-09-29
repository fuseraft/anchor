using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>Runs turns: stream a response, execute its tool calls through the gate, repeat until the model answers in text.</summary>
public sealed class Agent(IChatClient client, Toolbox toolbox, string systemPrompt, Action<AgentEvent> emit,
    ChatOptions? options = null, Limits? limits = null, Compactor? compactor = null)
{
    IChatClient _client = client;
    ChatOptions _options = WithTools(options, toolbox);
    readonly Limits _limits = limits ?? new Limits();
    bool _warnedFull;

    public List<ChatMessage> History { get; } = [];

    public IChatClient Client => _client;

    public ChatOptions Options => _options;

    public string SystemPrompt { get; set; } = systemPrompt;

    /// <summary>Size of the last request plus its reply, as reported by the provider; null when unknown.</summary>
    public long? LastContextTokens { get; private set; }

    public long ContextTokens => LastContextTokens ?? Messages.EstimateTokens(Context()) + 1_000;

    /// <summary>Switches model/provider; history carries over.</summary>
    public void Use(IChatClient newClient, ChatOptions newOptions)
    {
        _client = newClient;
        _options = WithTools(newOptions, toolbox);
    }

    /// <summary>Summarizes older turns now; the last turn stays verbatim.</summary>
    public async Task<Compaction?> CompactAsync(CancellationToken ct)
    {
        if (compactor is null)
            return null;
        var result = await compactor.CompactAsync(_client, _options, History, ct);
        if (result.Outcome == CompactOutcome.Compacted)
        {
            LastContextTokens = null;
            emit(new Compacted(result.Before, result.After));
        }
        return result;
    }

    public async Task<TurnEnd> RunTurnAsync(string input, CancellationToken ct)
    {
        var userMessage = new ChatMessage(ChatRole.User, input);
        History.Add(userMessage);
        _warnedFull = false;

        var guard = new LoopGuard(_limits);
        var usage = new UsageDetails();
        var overflowRetried = false;
        List<ChatResponseUpdate> streamed = [];
        List<AIContent>? results = null;

        try
        {
            while (true)
            {
                streamed = [];
                try
                {
                    _options.Tools = toolbox.Declarations;
                    await foreach (var update in _client.GetStreamingResponseAsync(Context(), _options, ct))
                    {
                        streamed.Add(update);
                        foreach (var content in update.Contents)
                            if (content is TextContent { Text.Length: > 0 } text)
                                emit(new TextDelta(text.Text));
                    }
                }
                catch (Exception e) when (!overflowRetried && streamed.Count == 0 && compactor is not null && Compactor.IsContextOverflow(e) && !ct.IsCancellationRequested)
                {
                    overflowRetried = true;
                    var overflow = ExceptionDispatchInfo.Capture(e);
                    emit(new Notice("The request was too large for the model's context; reducing it and retrying."));
                    if (!await ReduceContextAsync(force: true, ct))
                        overflow.Throw();
                    continue;
                }

                var response = streamed.ToChatResponse();
                streamed = [];
                if (response.Usage is { } u)
                {
                    usage.Add(u);
                    LastContextTokens = (u.InputTokenCount ?? 0) + (u.OutputTokenCount ?? 0);
                }
                else
                    LastContextTokens = null;
                History.AddRange(response.Messages);

                var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                if (calls.Count == 0)
                {
                    await ReduceContextAsync(force: false, ct);
                    return End(TurnEnd.Completed);
                }

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
                await ReduceContextAsync(force: false, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Recover(userMessage, streamed, results, "Cancelled by the user.");
            return End(TurnEnd.Cancelled);
        }
        catch (Exception e)
        {
            Recover(userMessage, streamed, results, "Not run: the turn failed.");
            return End(TurnEnd.Error, e.Message);
        }

        TurnEnd End(TurnEnd reason, string? detail = null)
        {
            emit(new UsageReport(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, usage.CachedInputTokenCount ?? 0));
            emit(new TurnEnded(reason, detail));
            return reason;
        }
    }

    // Summarizes older turns first; if that isn't enough (or there are none, as in one long turn), trims old tool
    // content, and as a last resort drops the oldest rounds of the current turn.
    // Forced after a provider overflow, when our estimate evidently undercounted.
    async Task<bool> ReduceContextAsync(bool force, CancellationToken ct)
    {
        if (compactor is null || (!force && !compactor.ShouldCompact(ContextTokens)))
            return false;

        var compacted = (await CompactAsync(ct))?.Outcome == CompactOutcome.Compacted;
        if (!force && !compactor.ShouldCompact(ContextTokens))
            return compacted;

        // Shed the real excess: provider counts include the system prompt and tool definitions, which the estimate doesn't see.
        var before = Messages.EstimateTokens(History);
        var target = before - Math.Max(force ? before / 2 : ContextTokens - compactor.EvictTarget, 0);

        var trimmed = Compactor.TrimToolContent(History, target);
        var afterTrim = Messages.EstimateTokens(History);
        if (trimmed > 0)
            emit(new Trimmed(trimmed, before, afterTrim));

        // Dropping loses more than trimming, so it only happens when trimming couldn't get back under the trigger.
        var dropAbove = force ? target : before - Math.Max(ContextTokens - compactor.TriggerTokens, 0);
        var dropped = afterTrim > dropAbove ? Compactor.DropRounds(History, target) : 0;
        if (dropped > 0)
            emit(new RoundsDropped(dropped, afterTrim, Messages.EstimateTokens(History)));

        if (trimmed + dropped > 0)
            LastContextTokens = null;
        else if (!compacted && !_warnedFull)
        {
            _warnedFull = true;
            emit(new Notice("Context is nearly full and nothing more can be summarized, trimmed or dropped. Consider /clear."));
        }
        return compacted || trimmed + dropped > 0;
    }

    IEnumerable<ChatMessage> Context() => [new ChatMessage(ChatRole.System, SystemPrompt), .. History];

    // Leaves history valid after an interrupted turn: keeps streamed text, answers every open call,
    // and drops the turn entirely if the model never produced anything.
    void Recover(ChatMessage userMessage, List<ChatResponseUpdate> streamed, List<AIContent>? results, string reason)
    {
        var partial = streamed.ToChatResponse().Text;
        if (partial.Length > 0)
            History.Add(new ChatMessage(ChatRole.Assistant, partial));

        if (History.Count > 0 && History[^1] is { Role: var role } last && role == ChatRole.Assistant)
        {
            var answered = (results ?? []).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
            var open = last.Contents.OfType<FunctionCallContent>().Where(c => !answered.Contains(c.CallId)).ToList();
            if (open.Count > 0)
                History.Add(new ChatMessage(ChatRole.Tool,
                    [.. results ?? [], .. open.Select(c => new FunctionResultContent(c.CallId, $"[anchor] {reason}"))]));
        }

        if (History.Count > 0 && ReferenceEquals(History[^1], userMessage))
            History.RemoveAt(History.Count - 1);
    }

    static ChatOptions WithTools(ChatOptions? options, Toolbox toolbox)
    {
        var o = options?.Clone() ?? new ChatOptions();
        o.Tools = toolbox.Declarations;
        return o;
    }
}
