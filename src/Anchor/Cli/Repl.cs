using System.Collections.Concurrent;
using Anchor.Core;
using Anchor.Providers;

namespace Anchor.Cli;

public sealed record ReplOptions(ProviderSettings Provider, string SessionsDir, bool Yolo, bool Resumed, long ContextWindow,
    IReadOnlyList<Skill> Skills, IReadOnlyList<AgentDefinition> Agents, SessionUsage Usage, Anchor.Mcp.McpHub Mcp,
    ModelSource Models, SubAgentRunner? SubAgents = null, string? SetUpModel = null)
{
    public static ReplOptions From(Harness h, Options options) =>
        new(h.Provider, h.SessionsDir, options.Yolo, options.Resume, h.ContextWindow, h.Skills, h.Agents, h.Usage, h.Mcp, h.Models, h.SubAgents,
            options.SetUpModel);
}

/// <summary>
/// The interactive loop: read a line, run it as a slash command, a shell escape, or an agent turn. On a full screen the
/// user can keep typing while a turn runs: a message joins the turn at its next step, and a command waits for it to end.
/// </summary>
public sealed partial class Repl
{
    const int ReplayTurns = 3;

    readonly Agent agent;
    readonly Gate gate;
    readonly SessionLog session;
    readonly Renderer renderer;
    readonly IReplScreen screen;
    readonly ReplOptions options;
    readonly ConcurrentQueue<string> _queued = new();
    readonly StatusLine status;
    CancellationTokenSource? _turn;
    ProviderSettings _provider;
    string? _until;
    string? _carried;
    bool _exit;

    public Repl(Agent agent, Gate gate, SessionLog session, Renderer renderer, IReplScreen screen, ReplOptions options)
    {
        (this.agent, this.gate, this.session, this.renderer, this.screen, this.options) = (agent, gate, session, renderer, screen, options);
        _provider = options.Provider;
        status = new StatusLine(agent, renderer, screen, options.ContextWindow, options.SubAgents, () => _provider.Model, () => _until);
        screen.Interrupt = Interrupt;
        agent.Attachments = (text, ct) => Mentions.AttachAsync(text, gate,
            (what, ok) => renderer.Line(ok ? renderer.Muted($"  + attached {what}") : renderer.Warning($"  {what}")), ct);
        // The status line follows the context through a turn, not just from one turn to the next.
        agent.ContextChanged = () => status.Redraw(agent.ContextTokens);
        // Sub-agents change on their own threads, where the history may be changing under an estimate; they keep the last count.
        if (options.SubAgents is { } subAgents)
            subAgents.Changed += () => status.Redraw(status.ShownTokens);
    }

    Workspace Workspace => gate.Workspace;

    public async Task<int> RunAsync()
    {
        var model = $"· {_provider.Model} · ";
        renderer.Line($"{renderer.Bold(renderer.Accent("anchor"))} {renderer.Muted(model + Renderer.ShortPath(Workspace.Root, Renderer.Width() - 8 - model.Length))}");
        if (options.Yolo)
            renderer.Line(renderer.Warning("--yolo: writes, commands and outside reads run without asking. Secret files and dangerous commands are still denied."));
        if (options.Resumed)
        {
            renderer.Line(renderer.Muted($"Resumed session {session.Id}."));
            renderer.Replay(agent.History, ReplayTurns);
        }
        else
        {
            var spaced = false;
            if (options.SetUpModel is { } setUp)
            {
                renderer.Line("");
                renderer.Line($"{renderer.Success("✓")} All set: anchor will use {setUp}.");
                renderer.Line(renderer.Muted("  Run /setup to change it."));
                spaced = true;
            }
            if (!options.Yolo && !SessionLog.Any(options.SessionsDir))
            {
                // The first session ever: say how approvals work before the first one appears.
                renderer.Line("");
                renderer.Line(renderer.Muted("Before anchor edits a file or runs a command, it shows you first."));
                renderer.Line(renderer.Muted("Press y to allow, n to decline, or a (when offered) to always allow."));
                spaced = true;
            }
            if (spaced)
                renderer.Line("");
        }
        renderer.Line(renderer.Muted("/help for commands, Ctrl+D to exit"));

        while (!_exit)
        {
            status.Set(working: false);
            var line = NextQueued();
            if (line is null)
            {
                line = await screen.ReadAsync(CancellationToken.None);
                if (line is null)
                    break;
                line = line.Trim();
                if (line.Length > 0)
                    screen.Echo(renderer.Prompt + line);
            }
            if (line.Length == 0)
                continue;

            if (line.StartsWith('!'))
                await ShellAsync(line[1..]);
            else if (line.StartsWith('/') && !line.Contains('\n'))
            {
                if (!await CommandAsync(line))
                    break;
            }
            else
                await TurnAsync(line);
            Save();
        }
        Save();
        return 0;
    }

    // A session that can't be written right now isn't worth losing the conversation over: what's missing goes out with the
    // next save.
    void Save()
    {
        try
        {
            session.Sync(agent.History);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            renderer.Line(renderer.Warning($"  Couldn't save the session: {e.Message} It will be tried again after the next turn."));
        }
    }

    async Task TurnAsync(string line)
    {
        if (!screen.FullScreen)
            renderer.Write("\n");
        gate.BeginTurn();
        status.Set(working: true);
        using var reading = new CancellationTokenSource();
        var typing = screen.FullScreen ? ReadWhileWorkingAsync(reading.Token) : Task.CompletedTask;
        bool completed;
        try
        {
            if (_until is null)
                completed = await CancellableAsync(ct => agent.RunTurnAsync(line, ct));
            else
                completed = await CancellableAsync(async ct =>
                {
                    if ((await Until.RunAsync(agent, gate, _until, line, ct)).Check is { } check and not CheckEnd.Passed)
                        renderer.Line(renderer.Warning($"  {Until.Describe(check, _until)}"));
                });
        }
        finally
        {
            await reading.CancelAsync();
            await typing;
        }
        AfterTurn(completed);
    }

    // Lines the user sends while the turn runs. Commands and shell escapes could change things under the turn, so they
    // wait; a message joins the turn. Ending the session (Ctrl+D) cancels the turn first.
    async Task ReadWhileWorkingAsync(CancellationToken ct)
    {
        try
        {
            while (await screen.ReadAsync(ct) is { } line)
            {
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith('!') || (line.StartsWith('/') && !line.Contains('\n')))
                {
                    _queued.Enqueue(line);
                    screen.Echo(renderer.Prompt + line + renderer.Muted("  (runs when this turn ends)"));
                }
                else
                {
                    agent.Interject(line);
                    screen.Echo(renderer.Prompt + line);
                }
            }
            _exit = true;
            Interrupt();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // False when the user cancelled it.
    async Task<bool> CancellableAsync(Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        _turn = cts;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            renderer.Line(renderer.Warning("  (cancelled)"));
        }
        finally
        {
            _turn = null;
        }
        return !cts.IsCancellationRequested;
    }

    bool Interrupt()
    {
        if (_turn is not { } turn)
            return false;
        turn.Cancel();
        return true;
    }

    // Messages the turn ended before reading become the next turn. Cancelling a turn drops everything typed during it.
    void AfterTurn(bool completed)
    {
        var unread = agent.TakeInterjections();
        if (completed)
        {
            _carried = unread;
            return;
        }
        var dropped = (unread is null ? 0 : 1) + _queued.Count;
        _queued.Clear();
        if (dropped > 0 && !_exit)
            renderer.Line(renderer.Muted("  (what you sent during the turn was dropped)"));
    }

    string? NextQueued()
    {
        if (_carried is { } carried)
        {
            _carried = null;
            renderer.Line(renderer.Muted("  (the turn ended before reading your message, so it starts the next one)"));
            return carried;
        }
        return _queued.TryDequeue(out var queued) ? queued : null;
    }

    async Task ShellAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
        await CancellableAsync(async ct =>
        {
            try
            {
                if (await screen.ShellAsync(command, Workspace.Root, ct) is var code and not 0)
                    renderer.Line(renderer.Muted($"exit {code}"));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                renderer.Line(renderer.Error(e.Message));
            }
        });
    }
}
