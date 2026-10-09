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
public sealed class Repl
{
    const int ReplayTurns = 3;

    readonly Agent agent;
    readonly Gate gate;
    readonly SessionLog session;
    readonly Renderer renderer;
    readonly IReplScreen screen;
    readonly ReplOptions options;
    readonly ConcurrentQueue<string> _queued = new();
    CancellationTokenSource? _turn;
    // The status line as of the last time the REPL drew it; sub-agent changes redraw it from their own threads.
    volatile string? _workingStatus;
    // Context at the start of the running turn, and how much the last turn added; they predict when compaction is near.
    long? _turnStart;
    long _lastGrowth;
    ProviderSettings _provider;
    string? _until;
    string? _carried;
    bool _exit;

    public Repl(Agent agent, Gate gate, SessionLog session, Renderer renderer, IReplScreen screen, ReplOptions options)
    {
        (this.agent, this.gate, this.session, this.renderer, this.screen, this.options) = (agent, gate, session, renderer, screen, options);
        _provider = options.Provider;
        screen.Interrupt = Interrupt;
        agent.Attachments = (text, ct) => Mentions.AttachAsync(text, gate,
            (what, ok) => renderer.Line(ok ? renderer.Muted($"  + attached {what}") : renderer.Warning($"  {what}")), ct);
        if (options.SubAgents is { } subAgents)
            subAgents.Changed += () =>
            {
                if (_workingStatus is { } status)
                    screen.Status(status + SubAgentStatus(subAgents.Running), working: true);
            };
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
            Status(working: false);
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
            session.Sync(agent.History);
        }
        session.Sync(agent.History);
        return 0;
    }

    async Task TurnAsync(string line)
    {
        if (!screen.FullScreen)
            Console.WriteLine();
        gate.BeginTurn();
        Status(working: true);
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

    void Status(bool working)
    {
        var tokens = agent.ContextTokens;
        if (working)
            _turnStart = tokens;
        else if (_turnStart is { } start)
        {
            _lastGrowth = Math.Max(0, tokens - start);
            _turnStart = null;
        }
        var used = $"{100.0 * tokens / options.ContextWindow:0}% context";
        if (agent.Compactor is { } c)
            used = ContextLevelOf(tokens, _lastGrowth, c.TriggerTokens) switch
            {
                ContextLevel.Compacting => renderer.Error(used),
                ContextLevel.Near => renderer.Warning(used),
                _ => renderer.Success(used),
            };
        var status = $"{renderer.Accent(_provider.Model)} · {used}" + (_until is null ? "" : $" · until {_until}");
        _workingStatus = working ? status : null;
        screen.Status(status, working);
    }

    public enum ContextLevel { Clear, Near, Compacting }

    /// <summary>
    /// How close the context is to compaction, which starts past <paramref name="trigger"/> tokens: Compacting when
    /// it's already past, so the next request compacts first; Near when a turn that adds as much as the last one
    /// (<paramref name="lastGrowth"/>) would get there; Clear otherwise.
    /// </summary>
    public static ContextLevel ContextLevelOf(long tokens, long lastGrowth, long trigger) =>
        tokens > trigger ? ContextLevel.Compacting : tokens + lastGrowth > trigger ? ContextLevel.Near : ContextLevel.Clear;

    /// <summary>The running sub-agents for the status line, such as " · agent-1: 7 calls, 12k tokens"; empty when none run.</summary>
    public static string SubAgentStatus(IReadOnlyList<SubAgentStats> running) =>
        string.Concat(running.Select(s => $" · {s.Id}: {s.ToolCalls} call{(s.ToolCalls == 1 ? "" : "s")}, {Tokens(s.Tokens)} tokens"));

    static string Tokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1_000}k",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };

    async Task SwitchModelAsync(string name)
    {
        var next = options.Models.Resolve(name);
        agent.Use(await options.Models.CreateAsync(next), Providers.Providers.Options(next));
        _provider = next;
        renderer.Line(renderer.Muted($"Model: {next.Model}"));
    }

    // Model lists for /model and /setup; one client serves every run of either.
    static readonly HttpClient SetupHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    Setup NewSetup() => new(screen.SetupIO, Anchor.Mcp.Keychain.Default(), new CredentialsFile(AnchorHome.Credentials),
        SetupHttp, Path.Combine(Config.Home, "config.json"));

    /// <summary>The slash commands, as /help lists them; the full screen suggests them from here.</summary>
    public static readonly (string Usage, string Description)[] Help =
    [
        ("/help", "list commands"),
        ("/model [name]", "pick a model and save it as the default, or switch to one for this session"),
        ("/setup", "choose a provider, save its key and pick a model"),
        ("/theme [name]", "list color themes, or switch to one for this session"),
        ("/context", "how full the context window is, and session token usage"),
        ("/agents", "list sub-agents"),
        ("/skills", "list skills"),
        ("/mcp", "list MCP servers; /mcp login|logout <server> to sign in or out"),
        ("/until [check]", "keep each turn going until the check command exits 0; /until off to stop"),
        ("/approvals", "list \"always\" answers saved for this directory; /approvals clear to forget them"),
        ("/compact", "summarize older turns now"),
        ("/copy [code]", "copy the last reply, or a code block from it, to the clipboard"),
        ("/undo", "revert the files changed in the last turn that changed any"),
        ("/sessions", "list sessions in this directory"),
        ("/clear", "forget the conversation"),
        ("/exit", "quit (or Ctrl+D)"),
    ];

    async Task<bool> CommandAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (parts[0])
        {
            case "/exit" or "/quit":
                return false;
            case "/clear":
                agent.History.Clear();
                gate.ForgetReads();
                screen.Clear();
                renderer.Line(renderer.Muted("History cleared."));
                break;
            case "/compact":
                await CancellableAsync(async ct =>
                {
                    var result = await agent.CompactAsync(ct);
                    if (result?.Outcome == CompactOutcome.NothingToCompact)
                        renderer.Line(renderer.Muted("Nothing to compact yet: the recent turns are kept as they are."));
                    else if (result?.Outcome == CompactOutcome.Rejected)
                        renderer.Line(renderer.Warning("The summary wasn't smaller than the conversation; history is unchanged."));
                });
                break;
            case "/context":
                var tokens = agent.ContextTokens;
                renderer.Line($"~{tokens:N0} of {options.ContextWindow:N0} tokens ({100.0 * tokens / options.ContextWindow:0}%), {agent.History.Count} messages" +
                              (agent.LastContextTokens is null ? renderer.Muted(" (estimated)") : ""));
                if (agent.Compactor is { } compactor)
                    renderer.Line(renderer.Muted($"Past ~{compactor.TriggerTokens:N0} tokens, anchor summarizes older turns before its next request (it trims old tool output, then drops the oldest steps, if that isn't enough)."));
                var u = options.Usage;
                renderer.Line(renderer.Muted($"Session so far, including sub-agents: in {u.Input:N0} · out {u.Output:N0} · cached {u.Cached:N0}"));
                break;
            case "/agents":
                renderer.Line($"agent (default)  {renderer.Muted("read-only: " + string.Join(", ", SubAgentRunner.ReadOnlyTools))}" +
                              renderer.Muted($"; up to {SubAgentRunner.MaxRunning} run at once, in the background"));
                foreach (var a in options.Agents)
                    renderer.Line($"{a.Name}  {renderer.Muted(a.Description)}" +
                                  renderer.Muted($" [tools: {(a.Tools is null ? "read-only" : string.Join(", ", a.Tools))}{(a.Model is null ? "" : $"; model: {a.Model}")}]"));
                renderer.Line(renderer.Muted("Define more in .agents/agents/<name>.md or ~/.anchor/agents/<name>.md."));
                break;
            case "/skills":
                if (options.Skills.Count == 0)
                    renderer.Line(renderer.Muted("No skills. Add one as .agents/skills/<name>/SKILL.md or ~/.anchor/skills/<name>/SKILL.md."));
                foreach (var s in options.Skills)
                    renderer.Line($"{s.Name}  {renderer.Muted(s.Description)}");
                break;
            case "/copy":
                Copy(parts.Length == 1 ? "" : parts[1]);
                break;
            case "/undo":
                var (restored, skipped) = gate.Undo();
                if (restored.Count == 0 && skipped.Count == 0)
                    renderer.Line(renderer.Muted("Nothing to undo."));
                foreach (var path in restored)
                    renderer.Line(renderer.Muted($"  restored {path}"));
                foreach (var path in skipped)
                    renderer.Line(renderer.Warning($"  skipped {path}: it changed after anchor wrote it"));
                if (restored.Count > 0)
                    agent.History.Add(Messages.Create(MessageKind.Note,
                        $"[anchor] The user undid your file changes. These files are back to their earlier content: {string.Join(", ", restored)}."));
                break;
            case "/sessions":
                foreach (var s in SessionLog.List(options.SessionsDir, Workspace.Root).Take(10))
                    renderer.Line($"{(s.Id == session.Id ? "*" : " ")} {s.Id}  {renderer.Muted(s.Updated.ToString("g"))}  {Truncate(s.Title, 60)}");
                renderer.Line(renderer.Muted("Resume one with: anchor --resume <id>"));
                break;
            case "/mcp" when parts.Length == 1:
                if (options.Mcp.Status.Count == 0)
                    renderer.Line(renderer.Muted("No MCP servers. Add them under mcpServers in ~/.anchor/config.json or in the project's .mcp.json."));
                foreach (var s in options.Mcp.Status)
                    renderer.Line($"{s.Name}  {s.State}{(s.Tools > 0 ? $", {s.Tools} tools" : "")}" + (s.Detail is null ? "" : renderer.Muted($"  {s.Detail}")));
                break;
            case "/mcp":
                var mcp = parts[1].Split(' ', 2, StringSplitOptions.TrimEntries);
                try
                {
                    if (mcp is ["login", var name])
                        await CancellableAsync(ct => options.Mcp.ConnectAsync(name, ct));
                    else if (mcp is ["logout", var name2])
                    {
                        await options.Mcp.LogoutAsync(name2);
                        renderer.Line(renderer.Muted($"Signed out of {name2}."));
                    }
                    else
                        renderer.Line(renderer.Error("Usage: /mcp, /mcp login <server>, /mcp logout <server>"));
                }
                catch (AnchorException e)
                {
                    renderer.Line(renderer.Error(e.Message));
                }
                break;
            case "/until" when parts.Length == 1:
                renderer.Line(_until is null
                    ? renderer.Muted("No check. /until <command> keeps each turn going until the command exits 0.")
                    : $"Check: {_until} {renderer.Muted("(/until off to clear)")}");
                break;
            case "/until" when parts[1] == "off":
                _until = null;
                renderer.Line(renderer.Muted("Check cleared."));
                break;
            case "/until":
                _until = parts[1];
                renderer.Line(renderer.Muted($"After each turn anchor runs `{_until}` and keeps working until it exits 0 (at most {Until.MaxRounds} rounds, or until a round changes no files)."));
                break;
            case "/model" when parts.Length == 1:
                try
                {
                    if (await NewSetup().PickModelAsync(_provider) is { } picked)
                    {
                        await SwitchModelAsync(picked);
                        renderer.Line(renderer.Muted("Saved as the default model."));
                    }
                }
                catch (AnchorException e)
                {
                    renderer.Line(renderer.Error(e.Message));
                }
                break;
            case "/model":
                try
                {
                    await SwitchModelAsync(parts[1]);
                }
                catch (AnchorException e)
                {
                    renderer.Line(renderer.Error(e.Message));
                }
                break;
            case "/theme" when parts.Length == 1:
                foreach (var theme in Theme.BuiltIn)
                    renderer.Line((theme.Name == Theme.Current.Name ? renderer.Accent("› ") : "  ") + theme.Name.PadRight(8) + renderer.Muted(theme.Description));
                break;
            case "/theme" when Theme.Named(parts[1]) is { } named:
                Theme.Current = named;
                renderer.Line(renderer.Muted($"Theme: {named.Name}, for this session. To keep a theme, pick it in /setup."));
                break;
            case "/theme":
                renderer.Line(renderer.Error($"Unknown theme {parts[1]}. Themes: {string.Join(", ", Theme.BuiltIn.Select(t => t.Name))}."));
                break;
            case "/setup":
                try
                {
                    if (await NewSetup().RunAsync() is { } model)
                    {
                        await SwitchModelAsync(model);
                        Theme.Current = Theme.Of(Config.Load());
                    }
                }
                catch (AnchorException e)
                {
                    renderer.Line(renderer.Error(e.Message));
                }
                break;
            case "/approvals" when parts.Length == 1:
                var saved = gate.SavedApprovals;
                if (saved.Programs.Count + saved.Tools.Count == 0)
                    renderer.Line(renderer.Muted("No approvals saved for this directory. Answer [a]lways to a command or MCP tool to save one."));
                foreach (var program in saved.Programs)
                    renderer.Line($"  command  {program}");
                foreach (var tool in saved.Tools)
                    renderer.Line($"  tool     {tool}");
                break;
            case "/approvals" when parts[1] == "clear":
                gate.ForgetApprovals();
                renderer.Line(renderer.Muted("Forgot every \"always\" answer for this directory, saved or from this session."));
                break;
            case "/help":
                foreach (var (usage, description) in Help.Skip(1))
                    renderer.Line($"{usage,-15} {description}");
                renderer.Line("""
                    !<command>      run a shell command yourself; the model never sees it
                    @<path>         attach a file or directory to the message (Tab completes)
                    Ctrl+C          cancel the running turn
                    Ctrl+F          find text in the conversation
                    Ctrl+R          search earlier messages
                    Ctrl+O          show the last tool output or diff in full; ←/→ for earlier ones

                    While anchor works you can keep typing: Enter sends the message into the
                    running turn, read after its current step. Commands wait for the turn to end.
                    """);
                break;
            default:
                renderer.Line(renderer.Error($"Unknown command {parts[0]}. Try /help."));
                break;
        }
        return true;
    }

    // The last reply as the model wrote it, or one of its code blocks, which the user picks when there are several.
    void Copy(string what)
    {
        if (what is not ("" or "code"))
        {
            renderer.Line(renderer.Error("Usage: /copy, or /copy code for a code block"));
            return;
        }
        var reply = agent.History.LastOrDefault(m => m.Role == Microsoft.Extensions.AI.ChatRole.Assistant && m.Text.Trim().Length > 0)?.Text.Trim();
        if (reply is null)
        {
            renderer.Line(renderer.Muted("Nothing to copy yet."));
            return;
        }
        var (text, name) = (reply, "the last reply");
        if (what == "code")
        {
            var blocks = Markdown.CodeBlocks(reply);
            if (blocks.Count == 0)
            {
                renderer.Line(renderer.Muted("The last reply has no code blocks."));
                return;
            }
            var pick = 0;
            if (blocks.Count > 1)
            {
                var choices = blocks.Select((b, i) => $"{i + 1}. {(b.Language.Length > 0 ? b.Language + ": " : "")}{Truncate(b.Code.Split('\n')[0].Trim(), 60)}").ToList();
                if (screen.SetupIO.Select("Which code block?", choices) is not { } chosen)
                    return;
                pick = choices.IndexOf(chosen);
            }
            (text, name) = (blocks[pick].Code, blocks.Count == 1 ? "the code block" : $"code block {pick + 1}");
        }
        var lines = text.Split('\n').Length;
        var tool = screen.Copy(text);
        renderer.Line(renderer.Muted($"Copied {name} ({lines} line{(lines == 1 ? "" : "s")})" +
                                     (tool is null ? " via OSC 52, if the terminal supports it." : ".")));
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

    static string Truncate(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;
}
