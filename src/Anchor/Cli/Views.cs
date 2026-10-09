using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Anchor.Cli;

/// <summary>Draws the Renderer's ANSI-styled text in a view, for the lines outside the transcript.</summary>
static class Styled
{
    public static readonly Attribute Plain = new(Color.None, Color.None);

    public static Attribute Accent => Of(Theme.StyleOf(Theme.Current.Accent) with { Bold = true }, Plain);

    public static Attribute Border => Of(Theme.StyleOf(Theme.Current.Border), Plain);

    public static List<Span> Parse(string ansi)
    {
        var transcript = new Transcript();
        transcript.Write(ansi);
        return transcript.Lines()[0];
    }

    /// <summary>One row of styled text, cut to the view's width with "…"; unstyled text is drawn in <paramref name="plain"/>.</summary>
    public static void Draw(View view, int row, List<Span> spans, Attribute plain)
    {
        var width = view.Viewport.Width;
        var overflow = spans.Sum(s => s.Text.GetColumns()) > width;
        var room = overflow ? width - 1 : width;
        var used = 0;
        view.Move(0, row);
        foreach (var span in spans)
        {
            var fits = new System.Text.StringBuilder();
            foreach (var rune in span.Text.EnumerateRunes())
            {
                var columns = Math.Max(0, rune.GetColumns());
                if (used + columns > room)
                    break;
                fits.Append(rune.ToString());
                used += columns;
            }
            view.SetAttribute(Of(span.Style, plain));
            view.AddStr(fits.ToString());
            if (used >= room)
                break;
        }
        view.SetAttribute(plain);
        if (overflow && width > 0)
        {
            view.AddStr("…");
            used++;
        }
        view.AddStr(new string(' ', Math.Max(0, width - used)));
    }

    public static Attribute Of(Style style, Attribute plain)
    {
        if (style == default)
            return plain;
        var fg = style.Color switch
        {
            30 => new Color(ColorName16.Black),
            31 => new Color(ColorName16.Red),
            32 => new Color(ColorName16.Green),
            33 => new Color(ColorName16.Yellow),
            34 => new Color(ColorName16.Blue),
            35 => new Color(ColorName16.Magenta),
            36 => new Color(ColorName16.Cyan),
            37 => new Color(ColorName16.Gray),
            90 => new Color(ColorName16.DarkGray),
            91 => new Color(ColorName16.BrightRed),
            92 => new Color(ColorName16.BrightGreen),
            93 => new Color(ColorName16.BrightYellow),
            94 => new Color(ColorName16.BrightBlue),
            95 => new Color(ColorName16.BrightMagenta),
            96 => new Color(ColorName16.BrightCyan),
            97 => new Color(ColorName16.White),
            _ => Color.None,
        };
        var text = (style.Bold ? TextStyle.Bold : TextStyle.None) | (style.Dim ? TextStyle.Faint : TextStyle.None)
            | (style.Italic ? TextStyle.Italic : TextStyle.None) | (style.Underline ? TextStyle.Underline : TextStyle.None);
        return new Attribute(fg, Color.None, text);
    }
}

/// <summary>Takes the prompt's place for approvals, ask_user and setup: a few lines of text, and a key handler.</summary>
sealed class PanelView : View
{
    List<(string Text, bool Highlighted)> _content = [];
    DateTime _quietFrom;
    TimeSpan _quiet;

    public PanelView() => CanFocus = true;

    public Func<Key, bool>? OnKey { get; set; }

    public int Rows { get; set; }

    public int Needed() => Math.Max(Rows, _content.Count);

    public void Show(List<(string Text, bool Highlighted)> content)
    {
        _content = content;
        SetNeedsDraw();
        SuperView?.SetNeedsLayout();
    }

    public void Reset(DateTime lastTyped, TimeSpan quiet)
    {
        _quietFrom = lastTyped;
        _quiet = quiet;
        _content = [];
        OnKey = null;
        foreach (var sub in SubViews.ToList())
        {
            Remove(sub);
            sub.Dispose();
        }
    }

    // Ctrl+C: answers as Esc would, even while keys are still being ignored.
    public void Dismiss() => OnKey?.Invoke(Key.Esc);

    protected override bool OnKeyDown(Key key)
    {
        // Keys still coming from typing into the prompt are swallowed, and keep the window open, until the typing stops.
        if (DateTime.UtcNow - _quietFrom < _quiet)
        {
            _quietFrom = DateTime.UtcNow;
            return true;
        }
        return OnKey?.Invoke(key) ?? false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        for (var i = 0; i < Viewport.Height; i++)
        {
            var (text, highlighted) = i < _content.Count ? _content[i] : ("", false);
            Styled.Draw(this, i, Styled.Parse(text), highlighted ? Styled.Accent : Styled.Plain);
        }
        return true;
    }
}

/// <summary>The line above the prompt; says how much is below when the transcript is scrolled up, or what the viewer shows.</summary>
sealed class RuleView : View
{
    int _below;
    string? _note;

    /// <summary>Drawn in place of the scroll count while set.</summary>
    public string? Note
    {
        get => _note;
        set
        {
            _note = value;
            SetNeedsDraw();
        }
    }

    public int Below
    {
        get => _below;
        set
        {
            _below = value;
            SetNeedsDraw();
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var label = _note ?? (_below > 0 ? $" ↓ {_below} more line{(_below == 1 ? "" : "s")} · Ctrl+End to follow " : "");
        Styled.Draw(this, 0, [new("──" + label + new string('─', Math.Max(0, Viewport.Width - 2 - label.GetColumns())), default)], Styled.Border);
        return true;
    }
}

/// <summary>The bottom line: a spinner while working, the REPL's status, and the keys that matter.</summary>
sealed class StatusView : View
{
    static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    string _text = "";
    string? _flash;
    int _frame;
    DateTime _since; // when the running turn started
    object? _timer;

    public bool Working => _working;

    volatile bool _working;

    public void Set(string text, bool working)
    {
        if (working && !_working)
            _since = DateTime.UtcNow;
        _text = text;
        _working = working;
        _flash = null;
        if (working && _timer is null && App is { } app)
            _timer = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                _frame++;
                SetNeedsDraw();
                if (_working)
                    return true;
                _timer = null;
                return false;
            });
        SetNeedsDraw();
    }

    /// <summary>How long a turn has run, as the status line shows it: 9s, 1m05s, 1h02m.</summary>
    public static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{(int)t.TotalSeconds}s";

    /// <summary>Shows a hint until the next key, or until <paramref name="expires"/> passes.</summary>
    public void Flash(string message, TimeSpan? expires = null)
    {
        _flash = message;
        SetNeedsDraw();
        if (expires is { } after && App is { } app)
            app.AddTimeout(after, () =>
            {
                if (ReferenceEquals(_flash, message))
                    ClearFlash();
                return false;
            });
    }

    public void ClearFlash()
    {
        if (_flash is null)
            return;
        _flash = null;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        List<Span> left = _flash is not null ? [new(_flash, Theme.StyleOf(Theme.Current.Warning))]
            : _working ? [new(Spinner[_frame % Spinner.Length], Theme.StyleOf(Theme.Current.Accent) with { Bold = true }), new($" working {Elapsed(DateTime.UtcNow - _since)} · ", default), .. Styled.Parse(_text)]
            : Styled.Parse(_text);
        var right = _working ? "Enter adds to the turn · Ctrl+C cancel" : "Enter send · Alt+Enter newline · PgUp/PgDn scroll · Ctrl+O output";
        var gap = Viewport.Width - left.Sum(s => s.Text.GetColumns()) - right.GetColumns();
        Styled.Draw(this, 0, gap >= 2 ? [.. left, new(new string(' ', gap) + right, default)] : left, Styled.Border);
        return true;
    }
}
