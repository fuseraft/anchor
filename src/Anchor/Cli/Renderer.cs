using Anchor.Core;

namespace Anchor.Cli;

/// <summary>A tool's whole result, or an approval's whole detail, for the TUI's output viewer (Ctrl+O).</summary>
public sealed record ToolOutput(string Title, string Text, bool Diff);

/// <summary>
/// Draws agent events on a terminal. In the TUI the model's Markdown is styled as it streams, the message redrawn as it
/// grows; a plain terminal can't take back what it printed, so it shows the Markdown as written.
/// </summary>
public sealed class Renderer(TextWriter output, bool color, bool streamText = true)
{
    readonly Transcript? _transcript = output as Transcript; // the TUI's
    readonly Transcript? _styled = color ? output as Transcript : null; // the TUI's, where Markdown is styled
    // Everything the renderer writes goes out under this, so lines from different threads never interleave.
    readonly Lock _lock = new();
    bool _midLine;
    Markdown? _message; // the model's message being streamed into it, while it's being redrawn
    bool _marked; // whether the model's current message has its anchor yet
    Action? _compacting; // takes the TUI's "compacting" line back out once whatever comes next is shown
    readonly bool _keeps = output is Transcript; // the TUI, which has a viewer for whole outputs
    readonly List<ToolOutput> _outputs = [];
    readonly Dictionary<string, string> _calls = []; // what each running call was started with, for its output's title

    /// <summary>How many outputs the TUI keeps for its viewer; older ones are let go.</summary>
    public const int KeptOutputs = 100;

    public static Renderer ForConsole() =>
        new(Console.Out, !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null);

    public void Render(AgentEvent e)
    {
        lock (_lock)
            Draw(e);
    }

    /// <summary>
    /// Runs <paramref name="write"/>, which must write whole lines, and returns an action that takes those lines back out of
    /// the TUI's transcript; on a plain terminal it can't, and does nothing. Nothing else is written in between.
    /// </summary>
    public Action Section(Action write)
    {
        lock (_lock)
        {
            if (_transcript is not null)
                return _transcript.Section(write);
            write();
            return () => { };
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> as it is, such as a prompt answered on the same line. Text for the terminal goes
    /// through the renderer, so it never lands in the middle of what another thread is drawing.
    /// </summary>
    public void Write(string text)
    {
        lock (_lock)
        {
            output.Write(text);
            output.Flush();
        }
    }

    /// <summary>Empties the TUI's transcript, for /clear, between whole writes.</summary>
    public void Clear()
    {
        lock (_lock)
            _transcript?.Clear();
    }

    void Draw(AgentEvent e)
    {
        EndCompacting();
        // Anything else the agent does ends its message; a sub-agent's lines go above it instead (see Line).
        if (e is not (TextDelta or SubAgentEvent))
        {
            EndMessage();
            _marked = false;
        }
        switch (e)
        {
            case TextDelta when !streamText:
                break;
            case TextDelta t when _styled is not null:
                if (_message is null)
                {
                    if (_midLine)
                        output.WriteLine();
                    _message = new Markdown(Width());
                }
                var (settled, open) = _message.Add(t.Text);
                // Settled lines stay put, so they take the anchor for good; the open line is redrawn, so it only borrows it.
                if (!_marked)
                {
                    settled = Marked(settled);
                    if (!_marked)
                        open = Marked(open, keep: false);
                }
                _styled.Stream(settled, open);
                _midLine = open.Length > 0 && !open.EndsWith('\n');
                break;
            case TextDelta t:
                output.Write(_transcript is null || _marked ? t.Text : Marked(t.Text));
                _midLine = !t.Text.EndsWith('\n');
                break;
            case ToolStarted t:
                Started("", t);
                Line("  " + Tool(t.Name, t.Summary));
                break;
            case ToolFinished t:
                Finished("", t);
                if (!t.Ok)
                    Line(Error($"    {FirstLine(t.Result)}"));
                break;
            case FileChanged f:
                Line(Muted($"  ✎ {f.Path} ") + Success($"+{f.Added}") + " " + Error($"-{f.Removed}"));
                break;
            case Compacting c:
                var line = Muted($"  ⟳ compacting older turns (~{c.Before:N0} tokens)…");
                if (_transcript is null)
                    Line(line); // a plain terminal can't take it back, so it stays
                else
                    _compacting = _transcript.Section(() => Line(line));
                break;
            case Compacted c:
                Line(Muted($"  ⟳ compacted older turns: ~{c.Before:N0} → ~{c.After:N0} tokens"));
                break;
            case Trimmed t:
                Line(Muted($"  ⟳ trimmed {t.Items} large old tool input{(t.Items == 1 ? "" : "s")}/output{(t.Items == 1 ? "" : "s")}: ~{t.Before:N0} → ~{t.After:N0} tokens"));
                break;
            case RoundsDropped d:
                Line(Muted($"  ⟳ dropped the {d.Rounds} oldest step{(d.Rounds == 1 ? "" : "s")} of this turn: ~{d.Before:N0} → ~{d.After:N0} tokens"));
                break;
            case Notice n:
                Line(Warning($"  {n.Message}"));
                break;
            case CheckRan { Passed: true } c:
                Line(Success($"  ✓ check passed: {c.Command}"));
                break;
            case CheckRan c:
                Line(Warning($"  ✗ check failed (round {c.Round} of {Until.MaxRounds}): {c.Command}"));
                break;
            case LoopWarning w:
                Line(Warning($"  ! {w.Message}"));
                break;
            case UsageReport u when u.Input + u.Output > 0:
                Line(Muted($"  in {u.Input:N0} · out {u.Output:N0}" + (u.CachedInput > 0 ? $" · cached {u.CachedInput:N0}" : "")));
                break;
            case SubAgentEvent s:
                Nested(s.Agent, s.Inner);
                break;
            case TurnEnded { Reason: TurnEnd.Completed } when _midLine:
                output.WriteLine();
                _midLine = false;
                break;
            case TurnEnded { Reason: TurnEnd.Cancelled }:
                Line(Warning("  (cancelled)"));
                break;
            case TurnEnded { Reason: TurnEnd.LoopStopped or TurnEnd.RoundLimit } t:
                Line(Warning($"  stopped: {t.Detail}"));
                break;
            case TurnEnded { Reason: TurnEnd.Error } t:
                Line(Error($"  error: {t.Detail}"));
                break;
        }
        output.Flush();
    }

    // A sub-agent's activity, indented under the agent call; its streamed text stays out of the way.
    void Nested(string agent, AgentEvent e)
    {
        var tag = "    " + AgentName($"[{agent}]");
        switch (e)
        {
            case ToolStarted t:
                Started(agent, t);
                Line($"{tag} {Tool(t.Name, t.Summary)}");
                break;
            case ToolFinished t:
                Finished(agent, t);
                if (!t.Ok)
                    Line(Error($"{tag}   {FirstLine(t.Result)}"));
                break;
            case FileChanged f:
                Line(tag + Muted($" ✎ {f.Path} ") + Success($"+{f.Added}") + " " + Error($"-{f.Removed}"));
                break;
            case UsageReport u when u.Input + u.Output > 0:
                Line(tag + Muted($" in {u.Input:N0} · out {u.Output:N0}"));
                break;
            case TurnEnded { Reason: not TurnEnd.Completed } t:
                Line(Warning($"{tag} stopped: {t.Detail ?? t.Reason.ToString()}"));
                break;
            case LoopWarning or Notice or Compacted or Trimmed or RoundsDropped:
                Line(tag + Muted($" {e switch { LoopWarning w => w.Message, Notice n => n.Message, _ => "reduced its context" }}"));
                break;
        }
    }

    void Started(string agent, ToolStarted t)
    {
        if (_keeps)
            _calls[agent + "\0" + t.CallId] = (agent.Length == 0 ? "" : $"[{agent}] ") + (t.Name + " " + t.Summary).Trim();
    }

    void Finished(string agent, ToolFinished t)
    {
        if (!_keeps)
            return;
        var key = agent + "\0" + t.CallId;
        var title = _calls.Remove(key, out var started) ? started : t.Name;
        Keep(new ToolOutput(t.Ok ? title : title + " (failed)", t.Result.Length == 0 ? "(no output)" : t.Result, Diff: false));
    }

    /// <summary>Keeps <paramref name="output"/> for the TUI's viewer; a plain terminal has no viewer, so it keeps nothing.</summary>
    public void Keep(ToolOutput output)
    {
        if (!_keeps)
            return;
        lock (_outputs)
        {
            _outputs.Add(output);
            if (_outputs.Count > KeptOutputs)
                _outputs.RemoveAt(0);
        }
    }

    /// <summary>The kept outputs, oldest first.</summary>
    public IReadOnlyList<ToolOutput> Outputs
    {
        get
        {
            lock (_outputs)
                return [.. _outputs];
        }
    }

    /// <summary>Set while the REPL waits for input, so background messages (MCP sign-in, failures) don't land on the prompt line.</summary>
    public bool AtPrompt { get; set; }

    void EndCompacting()
    {
        _compacting?.Invoke();
        _compacting = null;
    }

    void EndMessage()
    {
        if (_message is null)
            return;
        _message = null;
        _styled?.EndStream();
    }

    public void Line(string text)
    {
        lock (_lock)
        {
            EndCompacting();
            if (_styled is not null && _message is not null)
            {
                _styled.WriteLineAbove(text);
                return;
            }
            if (_midLine || AtPrompt)
                output.WriteLine();
            output.WriteLine(text);
            _midLine = false;
            if (AtPrompt)
                output.Write(Prompt);
            output.Flush();
        }
    }

    /// <summary>Re-shows the last <paramref name="turns"/> turns after a resume: the request, tool count, and final answer.</summary>
    public void Replay(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history, int turns)
    {
        var starts = Enumerable.Range(0, history.Count).Where(i => Messages.IsUserInput(history[i])).ToList();
        if (history.Count > 0 && Messages.Kind(history[0]) == MessageKind.Summary && (starts.Count <= turns))
            Line(Muted("  (earlier conversation summarized)"));
        foreach (var (start, n) in starts.Select((s, n) => (s, n)).TakeLast(turns))
        {
            var end = n + 1 < starts.Count ? starts[n + 1] : history.Count;
            var turn = history.Skip(start).Take(end - start).ToList();
            var calls = turn.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionCallContent>().Count();
            var answer = turn.LastOrDefault(m => m.Role == Microsoft.Extensions.AI.ChatRole.Assistant && m.Text.Length > 0)?.Text ?? "";
            Line("");
            Line(Prompt + FirstLine(turn[0].Text));
            if (calls > 0)
                Line(Muted($"  ↳ {calls} tool call{(calls == 1 ? "" : "s")}"));
            if (answer.Length > 0)
                Line((_transcript is null ? "" : Mark) + (_styled is null ? answer.Trim() : Markdown.Render(answer.Trim(), Width())));
        }
    }

    public void Diff(string diff, int maxLines = 80)
    {
        var lines = diff.TrimEnd('\n').Split('\n');
        foreach (var line in lines.Take(maxLines))
            Line("    " + (line.StartsWith('+') ? Success(line) : line.StartsWith('-') ? Error(line) : line.StartsWith("@@") ? Muted(line) : line));
        if (lines.Length > maxLines)
            Line(Muted($"    ... {lines.Length - maxLines} more lines" + (_keeps ? " (Ctrl+O shows them all)" : "")));
    }

    /// <summary>What starts each of the model's messages in the TUI, so they stand apart from everything else.</summary>
    string Mark => Accent("⚓\uFE0E") + " "; // the text style, which takes the accent color rather than drawing as an emoji

    // The text with the anchor before its first line that isn't blank; unchanged while it's all blank.
    string Marked(string text, bool keep = true)
    {
        var start = 0;
        while (start < text.Length && text[start] is '\n' or '\r')
            start++;
        if (start == text.Length)
            return text;
        _marked = keep;
        return text[..start] + Mark + text[start..];
    }

    /// <summary>The caret before what the user sends, in the accent color.</summary>
    public string Prompt => Bold(Accent("›")) + " ";

    /// <summary>anchor's own color, for the caret, the title and the spinner.</summary>
    public string Accent(string s) => Paint(Theme.Current.Accent, s);

    /// <summary>The question an approval asks, with the keys that answer it picked out.</summary>
    public string AllowPrompt(string? alwaysLabel) =>
        Warning("Allow?") + " " + Key('y', "es") + " " + Key('n', "o") + (alwaysLabel is null ? "" : " " + Key('a', "lways") + Muted(": " + alwaysLabel));

    /// <summary>How an approval was answered, for the transcript.</summary>
    public string Answered(Answer answer) =>
        Muted("  Allow? ") + (answer == Answer.No ? Error("no") : Success(answer.ToString().ToLowerInvariant()));

    string Key(char key, string rest) => Bold($"[{key}]") + rest;

    // A tool call: its name stands out, what it was given doesn't.
    string Tool(string name, string summary) => ToolName("↳ " + name) + (summary.Length == 0 ? "" : Muted(" " + summary));

    string ToolName(string s) => Paint(Theme.Current.Tool, s);

    string AgentName(string s) => Paint(Theme.Current.Agent, s);

    public string Success(string s) => Paint(Theme.Current.Success, s);

    public string Warning(string s) => Paint(Theme.Current.Warning, s);

    public string Error(string s) => Paint(Theme.Current.Error, s);

    public string Muted(string s) => Paint(Theme.Current.Muted, s);

    public string Bold(string s) => Paint("1", s);

    string Paint(string code, string s) => color && code.Length > 0 ? $"\e[{code}m{s}\e[0m" : s;

    /// <summary>A path as people write it: ~ for the home directory, and the start cut to "…/" when it's longer than
    /// <paramref name="max"/>, keeping as many trailing folders as fit.</summary>
    public static string ShortPath(string path, int max = int.MaxValue)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && (path == home || path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            path = "~" + path[home.Length..];
        if (path.Length <= max)
            return path;
        var sep = Path.DirectorySeparatorChar;
        var parts = path.Split(sep);
        var tail = parts[^1];
        for (var i = parts.Length - 2; i > 0 && 2 + parts[i].Length + 1 + tail.Length <= max; i--)
            tail = parts[i] + sep + tail;
        return "…" + sep + tail;
    }

    /// <summary>The terminal's width, or 80 when there's no terminal to ask.</summary>
    public static int Width()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : 80;
        }
        catch (IOException)
        {
            return 80;
        }
    }

    static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0];
        return line.Length > 200 ? line[..197] + "..." : line;
    }
}
