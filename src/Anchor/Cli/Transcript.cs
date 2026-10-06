using System.Text;
using Terminal.Gui.Text;

namespace Anchor.Cli;

/// <summary>How a run of transcript text looks: an SGR foreground (31-37, or 0 for the default), bold, dim.</summary>
public readonly record struct Style(int Color, bool Bold, bool Dim);

public readonly record struct Span(string Text, Style Style);

/// <summary>
/// What the TUI shows above the prompt. The Renderer writes ANSI-styled text into it exactly as it does to a terminal,
/// so every command and event looks the same; this reads the SGR codes back into styled lines.
/// </summary>
public sealed class Transcript : TextWriter
{
    readonly List<List<Span>> _lines = [[]];
    readonly StringBuilder _escape = new();
    Style _style;
    bool _inEscape;

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Raised after new text arrives, on the writing thread.</summary>
    public event Action? Changed;

    /// <summary>Bumped on every write, so a view knows whether its wrapped copy is stale.</summary>
    public int Version { get; private set; }

    /// <summary>The transcript as ANSI text, for printing to the terminal after the TUI closes.</summary>
    public string Ansi(bool color)
    {
        var sb = new StringBuilder();
        lock (_lines)
            foreach (var (line, i) in _lines.Select((l, i) => (l, i)))
            {
                foreach (var span in line)
                    sb.Append(color && span.Style != default ? $"{Sgr(span.Style)}{span.Text}\e[0m" : span.Text);
                if (i < _lines.Count - 1 || line.Count > 0)
                    sb.Append('\n');
            }
        return sb.ToString();
    }

    static string Sgr(Style style) =>
        "\e[" + string.Join(';', new[] { style.Bold ? "1" : null, style.Dim ? "2" : null, style.Color > 0 ? style.Color.ToString() : null }.OfType<string>()) + "m";

    /// <summary>
    /// Writes a whole line above the one still being streamed, if any, so what the user sent mid-sentence doesn't
    /// split the model's sentence in two.
    /// </summary>
    public void WriteLineAbove(string text)
    {
        lock (_lines)
        {
            var open = _lines[^1];
            if (open.Count == 0)
            {
                WriteLine(text);
                return;
            }
            var style = _style;
            _lines[^1] = [];
            _style = default;
            WriteLine(text);
            _lines.RemoveAt(_lines.Count - 1);
            _lines.Add(open);
            _style = style;
        }
        Changed?.Invoke();
    }

    public int Count
    {
        get
        {
            lock (_lines)
                return _lines.Count;
        }
    }

    /// <summary>Bumped when lines are removed, so a view knows to rebuild its wrapped copy instead of extending it.</summary>
    public int Removals { get; private set; }

    /// <summary>
    /// Runs <paramref name="write"/>, which must write whole lines, and returns an action that takes those lines back
    /// out. The Renderer's lock is held throughout, so no other output lands among them.
    /// </summary>
    public Action Section(Action write)
    {
        HashSet<List<Span>> written;
        lock (this)
        {
            int from;
            lock (_lines)
                from = _lines.Count - 1;
            write();
            lock (_lines)
                written = new(_lines.Skip(from).Take(_lines.Count - 1 - from), ReferenceEqualityComparer.Instance);
        }
        return () =>
        {
            lock (_lines)
            {
                if (_lines.RemoveAll(written.Contains) == 0)
                    return;
                Version++;
                Removals++;
            }
            Changed?.Invoke();
        };
    }

    /// <summary>A copy of lines <paramref name="from"/> onward, safe to use while writes continue.</summary>
    public List<List<Span>> Lines(int from = 0)
    {
        lock (_lines)
            return [.. _lines.Skip(from).Select(line => line.ToList())];
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        lock (_lines)
        {
            var text = new StringBuilder();
            foreach (var c in value)
            {
                if (_inEscape)
                {
                    _escape.Append(c);
                    if (c is >= '@' and <= '~' && _escape.Length > 1)
                    {
                        _inEscape = false;
                        if (c == 'm')
                            Apply(_escape.ToString(1, _escape.Length - 2));
                    }
                    continue;
                }
                switch (c)
                {
                    case '\e':
                        Flush(text);
                        _inEscape = true;
                        _escape.Clear();
                        break;
                    case '\n':
                        Flush(text);
                        _lines.Add([]);
                        break;
                    case '\r':
                        break;
                    default:
                        text.Append(c);
                        break;
                }
            }
            Flush(text);
            Version++;
        }
        Changed?.Invoke();
    }

    public override void WriteLine(string? value) => Write(value + "\n");

    void Flush(StringBuilder text)
    {
        if (text.Length == 0)
            return;
        var line = _lines[^1];
        if (line.Count > 0 && line[^1].Style == _style)
            line[^1] = new Span(line[^1].Text + text, _style);
        else
            line.Add(new Span(text.ToString(), _style));
        text.Clear();
    }

    // The subset of SGR the Renderer and Picker use: reset, bold, dim, normal intensity, and the 8 foreground colors.
    void Apply(string parameters)
    {
        foreach (var p in parameters.Split(';'))
        {
            _style = (int.TryParse(p, out var n) ? n : 0) switch
            {
                0 => default,
                1 => _style with { Bold = true },
                2 => _style with { Dim = true },
                22 => _style with { Bold = false, Dim = false },
                >= 30 and <= 37 and var color => _style with { Color = color },
                39 => _style with { Color = 0 },
                _ => _style,
            };
        }
    }

    /// <summary>Breaks a line into rows of at most <paramref name="width"/> columns, at spaces where it can.</summary>
    public static List<List<Span>> Wrap(List<Span> line, int width)
    {
        width = Math.Max(1, width);
        var cells = new List<(Rune Rune, Style Style, int Columns)>();
        foreach (var span in line)
            foreach (var rune in span.Text.EnumerateRunes())
                cells.Add((rune, span.Style, Math.Max(0, rune.GetColumns())));

        List<List<Span>> rows = [];
        var start = 0;
        while (start < cells.Count || rows.Count == 0)
        {
            int end = start, used = 0, lastSpace = -1;
            while (end < cells.Count && used + cells[end].Columns <= width)
            {
                if (cells[end].Rune.Value == ' ')
                    lastSpace = end;
                used += cells[end].Columns;
                end++;
            }
            if (end == start && end < cells.Count)
                end++; // a character wider than the row still has to go somewhere
            else if (end < cells.Count && lastSpace > start && !LongerThan(cells, lastSpace + 1, width))
                end = lastSpace + 1;
            rows.Add(Spans(cells, start, end));
            start = end;
            if (start >= cells.Count)
                break;
        }
        return rows;
    }

    // A word that can't fit on any row is broken where the row ends instead of being moved to the next one.
    static bool LongerThan(List<(Rune Rune, Style Style, int Columns)> cells, int from, int width)
    {
        var columns = 0;
        for (var i = from; i < cells.Count && cells[i].Rune.Value != ' '; i++)
            if ((columns += cells[i].Columns) > width)
                return true;
        return false;
    }

    static List<Span> Spans(List<(Rune Rune, Style Style, int Columns)> cells, int start, int end)
    {
        List<Span> spans = [];
        var text = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            if (i > start && cells[i].Style != cells[i - 1].Style)
            {
                spans.Add(new Span(text.ToString(), cells[i - 1].Style));
                text.Clear();
            }
            text.Append(cells[i].Rune.ToString());
        }
        if (text.Length > 0)
            spans.Add(new Span(text.ToString(), cells[end - 1].Style));
        return spans;
    }
}
