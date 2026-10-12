using Anchor.Core;

namespace Anchor.Cli;

// The slash commands.
public sealed partial class Repl
{
    async Task SwitchModelAsync(string name)
    {
        var next = options.Models.Resolve(name);
        agent.Use(await options.Models.CreateAsync(next), Providers.Providers.Options(next, Effort));
        _provider = next;
        renderer.Line(renderer.Muted($"Model: {ModelLabel}"));
    }

    Microsoft.Extensions.AI.ReasoningEffort? Effort => agent.Options.Reasoning?.Effort;

    /// <summary>The model, with the effort when one is set: claude-opus-5-5 (high).</summary>
    string ModelLabel => Effort is null ? _provider.Model : $"{_provider.Model} ({Providers.Providers.EffortName(Effort)})";

    // Model lists for /model and /setup; one client serves every run of either.
    static readonly HttpClient SetupHttp = Http.Client(TimeSpan.FromSeconds(30));

    Setup NewSetup() => new(screen.SetupIO, Anchor.Mcp.Keychain.Default(), new CredentialsFile(AnchorHome.Credentials),
        SetupHttp, Path.Combine(Config.Home, "config.json"));

    /// <summary>The slash commands, as /help lists them; the full screen suggests them from here.</summary>
    public static readonly (string Usage, string Description)[] Help =
    [
        ("/help", "list commands"),
        ("/model [name]", "pick a model and save it as the default, or switch to one for this session"),
        ("/effort [level]", "show how hard the model thinks, or set it for this session: none, low, medium, high, xhigh, default"),
        ("/setup", "choose a provider, save its key and pick a model"),
        ("/theme [name]", "list color themes, switch to one for this session, or save <name> the current colors as a theme file"),
        ("/dock [side]", "list where the prompt can go, or move it to the top or bottom for this session"),
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

    // Commands report what went wrong for the user (an unknown model, a failed sign-in) and the session goes on.
    async Task<bool> CommandAsync(string line)
    {
        try
        {
            return await RunCommandAsync(line);
        }
        catch (AnchorException e)
        {
            renderer.Line(renderer.Error(e.Message));
            return true;
        }
    }

    async Task<bool> RunCommandAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (parts[0])
        {
            case "/exit" or "/quit":
                return false;
            case "/clear":
                agent.Clear();
                gate.ForgetReads();
                screen.Clear();
                renderer.Line(renderer.Muted("History cleared."));
                break;
            case "/compact":
                status.Set(working: true, doing: "compacting");
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
                await CopyAsync(parts.Length == 1 ? "" : parts[1]);
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
                    agent.AddNote(
                        $"[anchor] The user undid your file changes. These files are back to their earlier content: {string.Join(", ", restored)}.");
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
                if (mcp is ["login", var name])
                    await CancellableAsync(ct => options.Mcp.ConnectAsync(name, ct));
                else if (mcp is ["logout", var name2])
                {
                    await options.Mcp.LogoutAsync(name2);
                    renderer.Line(renderer.Muted($"Signed out of {name2}."));
                }
                else
                    renderer.Line(renderer.Error("Usage: /mcp, /mcp login <server>, /mcp logout <server>"));
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
                if (await NewSetup().PickModelAsync(_provider) is { } picked)
                {
                    await SwitchModelAsync(picked);
                    renderer.Line(renderer.Muted("Saved as the default model."));
                }
                break;
            case "/model":
                await SwitchModelAsync(parts[1]);
                break;
            case "/effort" when parts.Length == 1:
                renderer.Line(renderer.Muted($"Effort: {Providers.Providers.EffortName(Effort)}. Levels: {Providers.Providers.EffortNames}."));
                break;
            case "/effort":
                agent.Use(agent.Client, Providers.Providers.Options(_provider, Providers.Providers.ParseEffort(parts[1])));
                status.Refresh();
                renderer.Line(renderer.Muted($"Effort: {Providers.Providers.EffortName(Effort)}, for this session. To keep it, set \"effort\" under provider in the config."));
                break;
            case "/theme" when parts.Length == 1:
            {
                // Theme files are read again each time, so a change to one shows on the next /theme.
                var (files, warnings) = Theme.Load(Theme.Dir);
                List<Theme> themes = [.. Theme.BuiltIn, .. files];
                var width = themes.Max(t => t.Name.Length) + 2;
                foreach (var theme in themes)
                    renderer.Line((theme.Name == Theme.Current.Name ? renderer.Accent("› ") : "  ") + renderer.Swatch(theme, width) + "  " + renderer.Muted(theme.Description));
                foreach (var warning in warnings)
                    renderer.Line(renderer.Warning(warning));
                renderer.Line(renderer.Muted($"Theme files go in {Renderer.ShortPath(Theme.Dir)}; /theme save <name> starts one from the current colors."));
                break;
            }
            case "/theme" when parts[1].Split(' ', 2) is ["save", var saveAs]:
            {
                saveAs = saveAs.Trim().ToLowerInvariant();
                var (file, problem) = Theme.Save(Theme.Chosen, saveAs, Theme.Dir);
                renderer.Line(file is not null ? renderer.Muted($"Saved {Renderer.ShortPath(file)}. Change it, then /theme {saveAs} to see it.") : renderer.Error(problem!));
                break;
            }
            case "/theme" when parts[1] == "save":
                renderer.Line(renderer.Error("Name the theme: /theme save <name>."));
                break;
            case "/theme" when Theme.Load(Theme.Dir) is var (files, warnings) && Theme.Named(parts[1], [.. Theme.BuiltIn, .. files]) is { } named:
                Theme.Current = named;
                foreach (var warning in warnings.Where(w => w.StartsWith($"themes/{named.Name}.", StringComparison.OrdinalIgnoreCase)))
                    renderer.Line(renderer.Warning(warning));
                renderer.Line(renderer.Muted($"Theme: {named.Name}, for this session. To keep a theme, pick it in /setup."));
                break;
            case "/theme":
                renderer.Line(renderer.Error($"Unknown theme {parts[1]}. Themes: {string.Join(", ", Theme.All().Select(t => t.Name))}."));
                break;
            case "/dock" when !screen.FullScreen:
                renderer.Line(renderer.Muted("Only the full screen has a prompt to move."));
                break;
            case "/dock" when parts.Length == 1:
                foreach (var (dock, position, description) in Docks.All)
                    renderer.Line((dock == screen.Docked ? renderer.Accent("› ") : "  ") + position.PadRight(8) + renderer.Muted(description));
                break;
            case "/dock" when Docks.Named(parts[1]) is { } docked:
                screen.DockPrompt(docked);
                renderer.Line(renderer.Muted($"Prompt: {Docks.Name(docked)}, for this session. To keep it, set \"dock\" in the config."));
                break;
            case "/dock":
                renderer.Line(renderer.Error($"Unknown position {parts[1]}. Positions: {Docks.Names}."));
                break;
            case "/setup":
                if (await NewSetup().RunAsync() is { } model)
                {
                    await SwitchModelAsync(model);
                    Theme.Current = Theme.Of(Config.Load());
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
    async Task CopyAsync(string what)
    {
        if (what is not ("" or "code"))
        {
            renderer.Line(renderer.Error("Usage: /copy, or /copy code for a code block"));
            return;
        }
        if (agent.LastReply is not { } reply)
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
                if (await screen.SetupIO.SelectAsync("Which code block?", choices) is not { } chosen)
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

    static string Truncate(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;
}
