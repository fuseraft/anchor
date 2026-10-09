using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Cli;

/// <summary>-p: one turn, then exit. The answer goes to stdout; progress goes to stderr (or everything to stdout as JSON lines).</summary>
public static class PrintMode
{
    public static async Task<int> RunAsync(Harness h, string prompt, JsonEvents? json, TextWriter stdout, string? until = null, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource();
        var interrupted = false;
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            interrupted = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            // MCP startup doesn't count toward --timeout, but Ctrl+C still stops it.
            try
            {
                await h.McpReady.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return 130;
            }
            if (timeout is { } limit)
                cts.CancelAfter(limit);
            h.Gate.BeginTurn();
            TurnEnd end;
            CheckEnd? check = null;
            try
            {
                (end, check) = until is null
                    ? (await h.Agent.RunTurnAsync(prompt, cts.Token), null)
                    : await Until.RunAsync(h.Agent, h.Gate, until, prompt, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                end = TurnEnd.Cancelled; // during the --until check
            }
            h.Session.Sync(h.Agent.History);
            var timedOut = end == TurnEnd.Cancelled && !interrupted && timeout is not null;

            var answer = end == TurnEnd.Completed ? h.Agent.LastReply ?? "" : "";
            if (json is not null)
                json.Write(new JsonObject
                {
                    ["type"] = "result",
                    ["status"] = timedOut ? "timed_out" : JsonEvents.Snake(end.ToString()),
                    ["text"] = answer,
                    ["check"] = check is null ? null : JsonEvents.Snake(check.Value.ToString()),
                    ["files_changed"] = new JsonArray([.. h.Gate.TurnChanges.Select(f => (JsonNode)f)]),
                    ["session"] = h.Session.Id,
                    ["usage"] = new JsonObject { ["input"] = h.Usage.Input, ["output"] = h.Usage.Output, ["cached"] = h.Usage.Cached },
                });
            else if (answer.Length > 0)
                stdout.WriteLine(answer);

            return end switch
            {
                TurnEnd.Completed when check is not (null or CheckEnd.Passed) => 4,
                TurnEnd.Completed => 0,
                TurnEnd.LoopStopped => 3,
                TurnEnd.RoundLimit => 5,
                TurnEnd.Cancelled when timedOut => 124,
                TurnEnd.Cancelled => 130,
                _ => 1,
            };
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}

/// <summary>
/// --json without -p: a line protocol for editors. Requests on stdin: <c>{"type":"user_input","text":…}</c>,
/// <c>{"type":"approval_response","id":…,"answer":"yes|no|always"}</c>, <c>{"type":"question_response","id":…,"answer":…}</c>, <c>{"type":"cancel"}</c>. Events go to stdout.
/// </summary>
public sealed class JsonMode(JsonEvents json, JsonApprover approver, TextReader stdin)
{
    public async Task<int> RunAsync(Harness h)
    {
        await h.McpReady;
        json.Write(Ready(h));
        Task? turn = null;
        CancellationTokenSource? turnCts = null;

        while (await stdin.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0)
                continue;
            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException e)
            {
                json.Error($"Invalid JSON: {e.Message}");
                continue;
            }

            switch ((string?)request?["type"])
            {
                case "user_input" when turn is { IsCompleted: false }:
                    json.Error("A turn is already running; send cancel first.");
                    break;
                case "user_input" when request!["text"] is JsonValue text && text.TryGetValue<string>(out var input) && input.Length > 0:
                    turnCts?.Dispose(); // its turn has ended
                    turnCts = new CancellationTokenSource();
                    var ct = turnCts.Token;
                    turn = Task.Run(() => TurnAsync(h, input, ct));
                    break;
                case "approval_response":
                    approver.Answer((string?)request!["id"], (string?)request["answer"]);
                    break;
                case "question_response":
                    approver.Reply((string?)request!["id"], request["answer"] is JsonValue a && a.TryGetValue<string>(out var reply) ? reply : null);
                    break;
                case "cancel":
                    turnCts?.Cancel();
                    break;
                default:
                    json.Error("Expected user_input (with text), approval_response, question_response, or cancel.");
                    break;
            }
        }

        turnCts?.Cancel();
        if (turn is not null)
            await turn;
        turnCts?.Dispose();
        return 0;
    }

    // Runs in the background, where nothing else would see it fail: whatever happens, the client hears about it and then
    // gets a ready event, so it's never left waiting for one.
    async Task TurnAsync(Harness h, string input, CancellationToken ct)
    {
        try
        {
            h.Gate.BeginTurn();
            await h.Agent.RunTurnAsync(input, ct);
            h.Session.Sync(h.Agent.History);
        }
        catch (Exception e)
        {
            json.Error(e is IOException or UnauthorizedAccessException ? $"Couldn't save the session: {e.Message}" : $"The turn failed: {CrashLog.Record(e)}");
        }
        finally
        {
            json.Write(Ready(h));
        }
    }

    static JsonObject Ready(Harness h) => new()
    {
        ["type"] = "ready",
        ["session"] = h.Session.Id,
        ["model"] = h.Provider.Model,
        ["tools"] = new JsonArray([.. h.Agent.Options.Tools?.Select(t => (JsonNode)t.Name) ?? []]),
    };
}

/// <summary>Writes agent events as JSON lines, one object per event.</summary>
public sealed class JsonEvents(TextWriter output)
{
    readonly Lock _lock = new();

    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Emit(AgentEvent e) => Write(Map(e));

    public void Error(string message) => Write(new JsonObject { ["type"] = "error", ["message"] = message });

    public void Write(JsonObject value)
    {
        lock (_lock)
        {
            output.WriteLine(value.ToJsonString(Options));
            output.Flush();
        }
    }

    public static JsonObject Map(AgentEvent e) => e switch
    {
        TextDelta t => new() { ["type"] = "text", ["text"] = t.Text },
        ToolStarted t => new() { ["type"] = "tool_start", ["id"] = t.CallId, ["name"] = t.Name, ["summary"] = t.Summary },
        ToolFinished t => new() { ["type"] = "tool_end", ["id"] = t.CallId, ["name"] = t.Name, ["ok"] = t.Ok, ["result"] = t.Result },
        FileChanged f => new() { ["type"] = "file_changed", ["path"] = f.Path, ["added"] = f.Added, ["removed"] = f.Removed },
        LoopWarning w => new() { ["type"] = "loop_warning", ["message"] = w.Message },
        Compacted c => Reduced("compacted", c.Before, c.After, null),
        Trimmed t => Reduced("trimmed", t.Before, t.After, t.Items),
        RoundsDropped d => Reduced("rounds_dropped", d.Before, d.After, d.Rounds),
        Notice n => new() { ["type"] = "notice", ["message"] = n.Message },
        CheckRan c => new() { ["type"] = "check", ["command"] = c.Command, ["round"] = c.Round, ["passed"] = c.Passed, ["output"] = c.Output },
        SubAgentEvent s => new() { ["type"] = "sub_agent", ["agent"] = s.Agent, ["event"] = Map(s.Inner) },
        UsageReport u => new() { ["type"] = "usage", ["input"] = u.Input, ["output"] = u.Output, ["cached"] = u.CachedInput },
        TurnEnded t => new() { ["type"] = "turn_end", ["reason"] = Snake(t.Reason.ToString()), ["detail"] = t.Detail },
        _ => new() { ["type"] = Snake(e.GetType().Name) },
    };

    static JsonObject Reduced(string kind, long before, long after, int? count) =>
        new() { ["type"] = "context_reduced", ["kind"] = kind, ["before"] = before, ["after"] = after, ["count"] = count };

    public static string Snake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

/// <summary>Sends approval_request events and waits for the matching approval_response.</summary>
public sealed class JsonApprover(JsonEvents json) : IApprover
{
    readonly ConcurrentDictionary<string, TaskCompletionSource<Answer>> _pending = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _questions = new();
    int _next;

    public async Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        var id = $"a{Interlocked.Increment(ref _next)}";
        var answer = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        json.Write(new JsonObject
        {
            ["type"] = "approval_request",
            ["id"] = id,
            ["title"] = request.Title,
            ["detail"] = request.Detail,
            ["always"] = request.AlwaysLabel,
        });
        try
        {
            return await answer.Task.WaitAsync(ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public bool CanAsk => true;

    /// <summary>Sends a question event and waits for the matching question_response; a null answer means dismissed.</summary>
    public async Task<string?> AskAsync(Question question, CancellationToken ct)
    {
        var id = $"q{Interlocked.Increment(ref _next)}";
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions[id] = answer;
        json.Write(new JsonObject
        {
            ["type"] = "question",
            ["id"] = id,
            ["text"] = question.Text,
            ["options"] = new JsonArray([.. question.Options.Select(o => (JsonNode)o)]),
            ["allowOther"] = question.AllowOther,
        });
        try
        {
            return await answer.Task.WaitAsync(ct);
        }
        finally
        {
            _questions.TryRemove(id, out _);
        }
    }

    public void Reply(string? id, string? answer)
    {
        if (id is null || !_questions.TryGetValue(id, out var pending))
        {
            json.Error($"No pending question with id '{id}'.");
            return;
        }
        pending.TrySetResult(string.IsNullOrWhiteSpace(answer) ? null : answer.Trim());
    }

    public void Answer(string? id, string? answer)
    {
        if (id is null || !_pending.TryGetValue(id, out var pending))
        {
            json.Error($"No pending approval with id '{id}'.");
            return;
        }
        pending.TrySetResult(answer switch
        {
            "yes" => Core.Answer.Yes,
            "always" => Core.Answer.Always,
            _ => Core.Answer.No,
        });
    }
}

/// <summary>For -p: nobody can answer, so anything that would ask is refused, and the reason is reported.</summary>
public sealed class RefusingApprover(Action<string> report) : IApprover
{
    public Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        report($"Refused (no one to ask in -p mode; use --allow or --yolo): {request.Title}");
        return Task.FromResult(Answer.No);
    }
}
