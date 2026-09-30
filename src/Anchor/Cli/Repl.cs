using System.Diagnostics;
using Anchor.Core;
using Anchor.Providers;

namespace Anchor.Cli;

public sealed record ReplOptions(ProviderSettings Provider, string SessionsDir, bool Yolo, bool Resumed, long ContextWindow,
    IReadOnlyList<Skill> Skills, IReadOnlyList<AgentDefinition> Agents, SessionUsage Usage, Anchor.Mcp.McpHub Mcp,
    ModelSource Models);

/// <summary>The interactive loop: read a line, run it as a slash command, a shell escape, or an agent turn.</summary>
public sealed class Repl(Agent agent, Gate gate, SessionLog session, Renderer renderer, ReplOptions options)
{
    const int ReplayTurns = 3;

    // A terminal delivers a paste in one burst, so input still waiting this long after Enter is part of the same paste.
    static readonly TimeSpan PasteGap = TimeSpan.FromMilliseconds(50);

    CancellationTokenSource? _turn;
    DateTime _lastIdleInterrupt;
    ProviderSettings _provider = options.Provider;
    string? _until;

    Workspace Workspace => gate.Workspace;

    public async Task<int> RunAsync()
    {
        Console.CancelKeyPress += OnCancel;
        renderer.Line($"{renderer.Bold("anchor")} {renderer.Dim($"· {_provider.Model} · {Workspace.Root}")}");
        if (options.Yolo)
            renderer.Line(renderer.Yellow("--yolo: writes, commands and outside reads run without asking. Secret files and dangerous commands are still denied."));
        if (options.Resumed)
        {
            renderer.Line(renderer.Dim($"Resumed session {session.Id}."));
            renderer.Replay(agent.History, ReplayTurns);
        }
        renderer.Line(renderer.Dim("/help for commands, Ctrl+D to exit"));

        while (true)
        {
            Console.Write("\n› ");
            renderer.AtPrompt = true;
            var line = await ReadInputAsync();
            renderer.AtPrompt = false;
            if (line is null)
                return 0;
            line = line.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('!'))
                await ShellAsync(line[1..]);
            else if (line.StartsWith('/') && !line.Contains('\n'))
            {
                if (!await CommandAsync(line))
                    return 0;
            }
            else
            {
                Console.WriteLine();
                gate.BeginTurn();
                if (_until is null)
                    await CancellableAsync(ct => agent.RunTurnAsync(line, ct));
                else
                    await CancellableAsync(async ct =>
                    {
                        if ((await Until.RunAsync(agent, gate, _until, line, ct)).Check is { } check and not CheckEnd.Passed)
                            renderer.Line(renderer.Yellow($"  {Until.Describe(check, _until)}"));
                    });
            }
            session.Sync(agent.History);
        }
    }

    async Task CancellableAsync(Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        _turn = cts;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            renderer.Line(renderer.Yellow("  (cancelled)"));
        }
        finally
        {
            _turn = null;
        }
    }

    // Reads one line, plus the rest of a multi-line paste, so a paste becomes one message instead of one turn per line.
    // An unterminated last line stays open for editing until Enter.
    static async Task<string?> ReadInputAsync()
    {
        var line = Console.ReadLine();
        if (line is null || Console.IsInputRedirected)
            return line;

        List<string> lines = [line];
        while (true)
        {
            await Task.Delay(PasteGap);
            if (!Console.KeyAvailable || Console.ReadLine() is not { } next)
                break;
            lines.Add(next);
        }
        return string.Join('\n', lines);
    }

    void SwitchModel(string name)
    {
        var next = options.Models.Resolve(name);
        agent.Use(options.Models.Create(next), Providers.Providers.Options(next));
        _provider = next;
        renderer.Line(renderer.Dim($"Model: {next.Model}"));
    }

    async Task<bool> CommandAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (parts[0])
        {
            case "/exit" or "/quit":
                return false;
            case "/clear":
                agent.History.Clear();
                renderer.Line(renderer.Dim("History cleared."));
                break;
            case "/compact":
                await CancellableAsync(async ct =>
                {
                    var result = await agent.CompactAsync(ct);
                    if (result?.Outcome == CompactOutcome.NothingToCompact)
                        renderer.Line(renderer.Dim("Nothing to compact yet: the recent turns are kept as they are."));
                    else if (result?.Outcome == CompactOutcome.Rejected)
                        renderer.Line(renderer.Yellow("The summary wasn't smaller than the conversation; history is unchanged."));
                });
                break;
            case "/context":
                var tokens = agent.ContextTokens;
                renderer.Line($"~{tokens:N0} of {options.ContextWindow:N0} tokens ({100.0 * tokens / options.ContextWindow:0}%), {agent.History.Count} messages" +
                              (agent.LastContextTokens is null ? renderer.Dim(" (estimated)") : ""));
                var u = options.Usage;
                renderer.Line(renderer.Dim($"Session so far, including sub-agents: in {u.Input:N0} · out {u.Output:N0} · cached {u.Cached:N0}"));
                break;
            case "/agents":
                renderer.Line($"agent (default)  {renderer.Dim("read-only: " + string.Join(", ", SubAgentRunner.ReadOnlyTools))}" +
                              renderer.Dim($"; up to {SubAgentRunner.MaxParallel} can run at once"));
                foreach (var a in options.Agents)
                    renderer.Line($"{a.Name}  {renderer.Dim(a.Description)}" +
                                  renderer.Dim($" [tools: {(a.Tools is null ? "read-only" : string.Join(", ", a.Tools))}{(a.Model is null ? "" : $"; model: {a.Model}")}]"));
                renderer.Line(renderer.Dim("Define more in .agents/agents/<name>.md or ~/.anchor/agents/<name>.md."));
                break;
            case "/skills":
                if (options.Skills.Count == 0)
                    renderer.Line(renderer.Dim("No skills. Add one as .agents/skills/<name>/SKILL.md or ~/.anchor/skills/<name>/SKILL.md."));
                foreach (var s in options.Skills)
                    renderer.Line($"{s.Name}  {renderer.Dim(s.Description)}");
                break;
            case "/undo":
                var (restored, skipped) = gate.Undo();
                if (restored.Count == 0 && skipped.Count == 0)
                    renderer.Line(renderer.Dim("Nothing to undo."));
                foreach (var path in restored)
                    renderer.Line(renderer.Dim($"  restored {path}"));
                foreach (var path in skipped)
                    renderer.Line(renderer.Yellow($"  skipped {path}: it changed after anchor wrote it"));
                if (restored.Count > 0)
                    agent.History.Add(Messages.Create(MessageKind.Note,
                        $"[anchor] The user undid your file changes. These files are back to their earlier content: {string.Join(", ", restored)}."));
                break;
            case "/sessions":
                foreach (var s in SessionLog.List(options.SessionsDir, Workspace.Root).Take(10))
                    renderer.Line($"{(s.Id == session.Id ? "*" : " ")} {s.Id}  {renderer.Dim(s.Updated.ToString("g"))}  {Truncate(s.Title, 60)}");
                renderer.Line(renderer.Dim("Resume one with: anchor --resume <id>"));
                break;
            case "/mcp" when parts.Length == 1:
                if (options.Mcp.Status.Count == 0)
                    renderer.Line(renderer.Dim("No MCP servers. Add them under mcpServers in ~/.anchor/config.json or in the project's .mcp.json."));
                foreach (var s in options.Mcp.Status)
                    renderer.Line($"{s.Name}  {s.State}{(s.Tools > 0 ? $", {s.Tools} tools" : "")}" + (s.Detail is null ? "" : renderer.Dim($"  {s.Detail}")));
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
                        renderer.Line(renderer.Dim($"Signed out of {name2}."));
                    }
                    else
                        renderer.Line(renderer.Red("Usage: /mcp, /mcp login <server>, /mcp logout <server>"));
                }
                catch (InvalidOperationException e)
                {
                    renderer.Line(renderer.Red(e.Message));
                }
                break;
            case "/until" when parts.Length == 1:
                renderer.Line(_until is null
                    ? renderer.Dim("No check. /until <command> keeps each turn going until the command exits 0.")
                    : $"Check: {_until} {renderer.Dim("(/until off to clear)")}");
                break;
            case "/until" when parts[1] == "off":
                _until = null;
                renderer.Line(renderer.Dim("Check cleared."));
                break;
            case "/until":
                _until = parts[1];
                renderer.Line(renderer.Dim($"After each turn anchor runs `{_until}` and keeps working until it exits 0 (at most {Until.MaxRounds} rounds, or until a round changes no files)."));
                break;
            case "/model" when parts.Length == 1:
                renderer.Line($"{_provider.Model} {renderer.Dim($"({_provider.Via ?? _provider.Provider})")}");
                break;
            case "/model":
                try
                {
                    SwitchModel(parts[1]);
                }
                catch (InvalidOperationException e)
                {
                    renderer.Line(renderer.Red(e.Message));
                }
                break;
            case "/setup":
                try
                {
                    var setup = new Setup(new ConsoleSetupIO(), Anchor.Mcp.Keychain.Default(), new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                        Path.Combine(Config.Home, "config.json"));
                    if (await setup.RunAsync() is { } model)
                        SwitchModel(model);
                }
                catch (InvalidOperationException e)
                {
                    renderer.Line(renderer.Red(e.Message));
                }
                break;
            case "/approvals" when parts.Length == 1:
                var saved = gate.SavedApprovals;
                if (saved.Programs.Count + saved.Tools.Count == 0)
                    renderer.Line(renderer.Dim("No approvals saved for this directory. Answer [a]lways to a command or MCP tool to save one."));
                foreach (var program in saved.Programs)
                    renderer.Line($"  command  {program}");
                foreach (var tool in saved.Tools)
                    renderer.Line($"  tool     {tool}");
                break;
            case "/approvals" when parts[1] == "clear":
                gate.ForgetApprovals();
                renderer.Line(renderer.Dim("Forgot every \"always\" answer for this directory, saved or from this session."));
                break;
            case "/help":
                renderer.Line("""
                    /model [name]   show or switch the model
                    /setup          choose a provider, save its key and pick a model
                    /context        how full the context window is, and session token usage
                    /agents         list sub-agents
                    /skills         list skills
                    /mcp            list MCP servers; /mcp login|logout <server> to sign in or out
                    /until [check]  keep each turn going until the check command exits 0; /until off to stop
                    /approvals      list "always" answers saved for this directory; /approvals clear to forget them
                    /compact        summarize older turns now
                    /undo           revert the files changed in the last turn that changed any
                    /sessions       list sessions in this directory
                    /clear          forget the conversation
                    /exit           quit (or Ctrl+D)
                    !<command>      run a shell command yourself; the model never sees it
                    Ctrl+C          cancel the running turn
                    """);
                break;
            default:
                renderer.Line(renderer.Red($"Unknown command {parts[0]}. Try /help."));
                break;
        }
        return true;
    }

    async Task ShellAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", command])
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh", ["-c", command]);
        psi.WorkingDirectory = Workspace.Root;
        try
        {
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                renderer.Line(renderer.Dim($"exit {process.ExitCode}"));
        }
        catch (Exception e)
        {
            renderer.Line(renderer.Red(e.Message));
        }
    }

    void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (_turn is { } turn)
        {
            turn.Cancel();
            return;
        }
        if (DateTime.UtcNow - _lastIdleInterrupt < TimeSpan.FromSeconds(2))
        {
            session.Sync(agent.History);
            Environment.Exit(0);
        }
        _lastIdleInterrupt = DateTime.UtcNow;
        Console.Write("\n(press Ctrl+C again or Ctrl+D to exit)\n› ");
    }

    static string Truncate(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;
}
