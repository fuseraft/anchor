using Anchor.Core;

namespace Anchor.Cli;

/// <summary>Draws agent events on a terminal.</summary>
public sealed class Renderer(TextWriter output, bool color)
{
    bool _midLine;

    public static Renderer ForConsole() =>
        new(Console.Out, !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null);

    public void Render(AgentEvent e)
    {
        switch (e)
        {
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
            case LoopWarning w:
                Line(Yellow($"  ! {w.Message}"));
                break;
            case UsageReport u when u.Input + u.Output > 0:
                Line(Dim($"  in {u.Input:N0} · out {u.Output:N0}" + (u.CachedInput > 0 ? $" · cached {u.CachedInput:N0}" : "")));
                break;
            case TurnEnded { Reason: TurnEnd.Cancelled }:
                Line(Yellow("  (cancelled)"));
                break;
            case TurnEnded { Reason: TurnEnd.LoopStopped } t:
                Line(Yellow($"  stopped: {t.Detail}"));
                break;
            case TurnEnded { Reason: TurnEnd.Error } t:
                Line(Red($"  error: {t.Detail}"));
                break;
        }
        output.Flush();
    }

    public void Line(string text)
    {
        if (_midLine)
            output.WriteLine();
        output.WriteLine(text);
        _midLine = false;
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
