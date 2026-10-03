using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>Work a turn starts that runs alongside it, such as background sub-agents. The turn waits for it before it ends.</summary>
public interface IBackground
{
    /// <summary>True while any of it is still running.</summary>
    bool Busy { get; }

    /// <summary>Stops whatever is still running and waits for it to end.</summary>
    Task StopAsync();
}

/// <summary>Runs turns: stream a response, execute its tool calls through the gate, repeat until the model answers in text.</summary>
public sealed class Agent(IChatClient client, Toolbox toolbox, string systemPrompt, Action<AgentEvent> emit,
    ChatOptions? options = null, Limits? limits = null, Compactor? compactor = null)
{
    IChatClient _client = client;
    ChatOptions _options = WithTools(options, toolbox);
    readonly Limits _limits = limits ?? new Limits();
    readonly ConcurrentQueue<string> _interjections = new();
    readonly ConcurrentQueue<string> _notes = new();
    readonly SemaphoreSlim _wake = new(0);
    bool _warnedFull;
    int _rounds;
    long _turnTokens;

    public List<ChatMessage> History { get; } = [];

    public IChatClient Client => _client;

    public ChatOptions Options => _options;

    public string SystemPrompt { get; set; } = systemPrompt;

    /// <summary>The most model requests this agent may make over its life (-p --max-rounds); null for no limit.</summary>
    public int? MaxRounds { get; set; }

    /// <summary>Size of the last request plus its reply, as reported by the provider; null when unknown.</summary>
    public long? LastContextTokens { get; private set; }

    public long ContextTokens => LastContextTokens ?? Messages.EstimateTokens(Context()) + 1_000;

    /// <summary>Tokens the current (or last) turn has used so far, input and output, as the provider reported them.</summary>
    public long TurnTokens => Interlocked.Read(ref _turnTokens);

    /// <summary>Work this agent's turns start in the background (sub-agents); a turn doesn't end while it runs.</summary>
    public IBackground? Background { get; set; }

    /// <summary>Adds a message the user typed while the turn runs; the model reads it after the current step's tool results.</summary>
    public void Interject(string text)
    {
        _interjections.Enqueue(text);
        _wake.Release();
    }

    /// <summary>Adds a note from anchor, such as a background sub-agent's report; the model reads it like an interjection.</summary>
    public void Notify(string text)
    {
        _notes.Enqueue(text);
        _wake.Release();
    }

    /// <summary>Takes the interjections not read yet, as one message; null when there are none.</summary>
    public string? TakeInterjections()
    {
        List<string> texts = [];
        while (_interjections.TryDequeue(out var text))
            texts.Add(text);
        return texts.Count == 0 ? null : string.Join("\n\n", texts);
    }

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
        Interlocked.Exchange(ref _turnTokens, 0);
        var overflowRetried = false;
        List<ChatResponseUpdate> streamed = [];
        List<AIContent>? results = null;

        try
        {
            while (true)
            {
                if (_rounds++ >= MaxRounds)
                    return await EndAsync(TurnEnd.RoundLimit, $"reached the limit of {MaxRounds} model requests");
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
                    Interlocked.Exchange(ref _turnTokens, (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0));
                    LastContextTokens = (u.InputTokenCount ?? 0) + (u.OutputTokenCount ?? 0);
                }
                else
                    LastContextTokens = null;
                History.AddRange(response.Messages);

                var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                if (calls.Count == 0)
                {
                    if (await AwaitBackgroundAsync(ct))
                    {
                        ReadInbox();
                        continue;
                    }
                    await ReduceContextAsync(force: false, ct);
                    return await EndAsync(TurnEnd.Completed);
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
                    return await EndAsync(TurnEnd.LoopStopped, stop);
                ReadInbox();
                await ReduceContextAsync(force: false, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Recover(userMessage, streamed, results, "Cancelled by the user.");
            return await EndAsync(TurnEnd.Cancelled);
        }
        catch (Exception e)
        {
            Recover(userMessage, streamed, results, "Not run: the turn failed.");
            return await EndAsync(TurnEnd.Error, e.Message);
        }

        // Nothing the turn started outlives it: whatever still runs is stopped, and reports not yet read are dropped.
        async Task<TurnEnd> EndAsync(TurnEnd reason, string? detail = null)
        {
            if (Background is { } background)
                await background.StopAsync();
            _notes.Clear();
            emit(new UsageReport(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, usage.CachedInputTokenCount ?? 0));
            emit(new TurnEnded(reason, detail));
            return reason;
        }
    }

    // The model has answered, but background work it started may still be running. Waits for that work to report, or
    // for the user to say something (such as asking how it's going). False when there's nothing more for the model.
    async Task<bool> AwaitBackgroundAsync(CancellationToken ct)
    {
        while (true)
        {
            // Busy is read first: a sub-agent leaves its report before it stops being busy, so none is missed.
            var busy = Background?.Busy == true;
            if (!_notes.IsEmpty || (busy && !_interjections.IsEmpty))
                return true;
            if (!busy)
                return false;
            await _wake.WaitAsync(ct);
        }
    }

    // Reports and other notes from anchor, then what the user typed, for the model to read next.
    void ReadInbox()
    {
        List<string> notes = [];
        while (_notes.TryDequeue(out var note))
            notes.Add(note);
        if (notes.Count > 0)
            History.Add(Messages.Create(MessageKind.Note, string.Join("\n\n", notes)));
        if (TakeInterjections() is { } typed)
            History.Add(new ChatMessage(ChatRole.User, typed));
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
