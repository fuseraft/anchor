using Anchor.Core;

namespace Anchor.Cli;

/// <summary>Draws agent events on a terminal.</summary>
public sealed class Renderer(TextWriter output, bool color, bool streamText = true)
{
    bool _midLine;

    public static Renderer ForConsole() =>
        new(Console.Out, !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null);

    public void Render(AgentEvent e)
    {
        switch (e)
        {
            case TextDelta when !streamText:
                break;
            case TextDelta t:
                output.Write(t.Text);
                _midLine = !t.Text.EndsWith('\n');
                break;
            case ToolStarted t:
                Line(Dim($"  ↳ {t.Name} {t.Summary}".TrimEnd()));
                break;
            case ToolFinished { Ok: false } t:
                Line(Red($"    {FirstLine(t.Result)}"));
                break;
            case FileChanged f:
                Line(Dim($"  ✎ {f.Path} ") + Green($"+{f.Added}") + " " + Red($"-{f.Removed}"));
                break;
            case Compacted c:
                Line(Dim($"  ⟳ compacted older turns: ~{c.Before:N0} → ~{c.After:N0} tokens"));
                break;
            case Trimmed t:
                Line(Dim($"  ⟳ trimmed {t.Items} large old tool input{(t.Items == 1 ? "" : "s")}/output{(t.Items == 1 ? "" : "s")}: ~{t.Before:N0} → ~{t.After:N0} tokens"));
                break;
            case RoundsDropped d:
                Line(Dim($"  ⟳ dropped the {d.Rounds} oldest step{(d.Rounds == 1 ? "" : "s")} of this turn: ~{d.Before:N0} → ~{d.After:N0} tokens"));
                break;
            case Notice n:
                Line(Yellow($"  {n.Message}"));
                break;
            case CheckRan { Passed: true } c:
                Line(Green($"  ✓ check passed: {c.Command}"));
                break;
            case CheckRan c:
                Line(Yellow($"  ✗ check failed (round {c.Round} of {Until.MaxRounds}): {c.Command}"));
                break;
            case LoopWarning w:
                Line(Yellow($"  ! {w.Message}"));
                break;
            case UsageReport u when u.Input + u.Output > 0:
                Line(Dim($"  in {u.Input:N0} · out {u.Output:N0}" + (u.CachedInput > 0 ? $" · cached {u.CachedInput:N0}" : "")));
                break;
            case SubAgentEvent s:
                Nested(s.Agent, s.Inner);
                break;
            case TurnEnded { Reason: TurnEnd.Cancelled }:
                Line(Yellow("  (cancelled)"));
                break;
            case TurnEnded { Reason: TurnEnd.LoopStopped or TurnEnd.RoundLimit } t:
                Line(Yellow($"  stopped: {t.Detail}"));
                break;
            case TurnEnded { Reason: TurnEnd.Error } t:
                Line(Red($"  error: {t.Detail}"));
                break;
        }
        output.Flush();
    }

    // A sub-agent's activity, indented under the agent call; its streamed text stays out of the way.
    void Nested(string agent, AgentEvent e)
    {
        var tag = $"    [{agent}]";
        switch (e)
        {
            case ToolStarted t:
                Line(Dim($"{tag} ↳ {t.Name} {t.Summary}".TrimEnd()));
                break;
            case ToolFinished { Ok: false } t:
                Line(Red($"{tag}   {FirstLine(t.Result)}"));
                break;
            case FileChanged f:
                Line(Dim($"{tag} ✎ {f.Path} ") + Green($"+{f.Added}") + " " + Red($"-{f.Removed}"));
                break;
            case UsageReport u when u.Input + u.Output > 0:
                Line(Dim($"{tag} in {u.Input:N0} · out {u.Output:N0}"));
                break;
            case TurnEnded { Reason: not TurnEnd.Completed } t:
                Line(Yellow($"{tag} stopped: {t.Detail ?? t.Reason.ToString()}"));
                break;
            case LoopWarning or Notice or Compacted or Trimmed or RoundsDropped:
                Line(Dim($"{tag} {e switch { LoopWarning w => w.Message, Notice n => n.Message, _ => "reduced its context" }}"));
                break;
        }
    }

    /// <summary>Set while the REPL waits for input, so background messages (MCP sign-in, failures) don't land on the prompt line.</summary>
    public bool AtPrompt { get; set; }

    public void Line(string text)
    {
        lock (output)
        {
            if (_midLine || AtPrompt)
                output.WriteLine();
            output.WriteLine(text);
            _midLine = false;
            if (AtPrompt)
                output.Write("› ");
        }
    }

    /// <summary>Re-shows the last <paramref name="turns"/> turns after a resume: the request, tool count, and final answer.</summary>
    public void Replay(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history, int turns)
    {
        var starts = Enumerable.Range(0, history.Count).Where(i => Messages.IsUserInput(history[i])).ToList();
        if (history.Count > 0 && Messages.Kind(history[0]) == MessageKind.Summary && (starts.Count <= turns))
            Line(Dim("  (earlier conversation summarized)"));
        foreach (var (start, n) in starts.Select((s, n) => (s, n)).TakeLast(turns))
        {
            var end = n + 1 < starts.Count ? starts[n + 1] : history.Count;
            var turn = history.Skip(start).Take(end - start).ToList();
            var calls = turn.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionCallContent>().Count();
            var answer = turn.LastOrDefault(m => m.Role == Microsoft.Extensions.AI.ChatRole.Assistant && m.Text.Length > 0)?.Text ?? "";
            Line("");
            Line(Bold("› ") + FirstLine(turn[0].Text));
            if (calls > 0)
                Line(Dim($"  ↳ {calls} tool call{(calls == 1 ? "" : "s")}"));
            if (answer.Length > 0)
                Line(answer.Trim());
        }
    }

    public void Diff(string diff, int maxLines = 80)
    {
        var lines = diff.TrimEnd('\n').Split('\n');
        foreach (var line in lines.Take(maxLines))
            Line("    " + (line.StartsWith('+') ? Green(line) : line.StartsWith('-') ? Red(line) : line.StartsWith("@@") ? Dim(line) : line));
        if (lines.Length > maxLines)
            Line(Dim($"    ... {lines.Length - maxLines} more lines"));
    }

    public string Green(string s) => Paint("32", s);

    public string Dim(string s) => Paint("2", s);

    public string Red(string s) => Paint("31", s);

    public string Yellow(string s) => Paint("33", s);

    public string Bold(string s) => Paint("1", s);

    string Paint(string code, string s) => color ? $"\e[{code}m{s}\e[0m" : s;

    static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0];
        return line.Length > 200 ? line[..197] + "..." : line;
    }
}
