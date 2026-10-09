using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Anchor.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Anchor.Cli;

/// <summary>
/// The full-screen REPL: the transcript on top, the prompt below it, and a status line. The REPL runs on a background
/// task and reaches the screen through IReplScreen and IApprover; everything that touches a view goes through Ui().
/// </summary>
public sealed class Tui : IReplScreen, IApprover
{
    const int MaxPromptRows = 10;

    // How long typing has to stop before a prompt takes keys, so a "y" typed into the draft never answers an approval.
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    // How long without a key before anchor guesses the user has looked away, and rings when it needs them.
    internal static readonly TimeSpan Away = TimeSpan.FromSeconds(10);

    readonly IApplication _app;
    readonly Transcript _transcript = new();
    readonly Channel<string?> _sent = Channel.CreateUnbounded<string?>();
    readonly SemaphoreSlim _panelTurn = new(1, 1);
    readonly Runnable _top;
    readonly TranscriptView _view;
    readonly Transcript _outputText = new();
    readonly TranscriptView _outputView;
    int? _output; // which of the Renderer's outputs the viewer shows, while it's open
    readonly RuleView _rule;
    readonly Label _caret;
    readonly PromptView _prompt;
    readonly PromptCompletion _completion = new();
    readonly SuggestView _suggest;
    readonly PanelView _panel;
    readonly StatusView _status;
    DateTime _lastInterrupt;
    DateTime _lastKey = DateTime.UtcNow;
    bool _bell = true;
    Theme _theme = Theme.Current; // what the views were last drawn in
    int _redrawQueued;

    public static bool Supported => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public Renderer Renderer { get; }

    public bool FullScreen => true;

    public Func<bool>? Interrupt { private get; set; }

    public ISetupIO SetupIO { get; }

    public bool CanAsk => true;

    Tui(IApplication app)
    {
        _app = app;
        Renderer = new Renderer(_transcript, color: Environment.GetEnvironmentVariable("NO_COLOR") is null);
        SetupIO = new TuiSetupIO(this);

        var plain = new Attribute(Color.None, Color.None);
        _top = new Runnable { Width = Dim.Fill(), Height = Dim.Fill() };
        _top.SetScheme(new Scheme(plain) { Focus = plain, Editable = plain, Active = plain });
        _view = new TranscriptView(_transcript) { X = 0, Y = 0, Width = Dim.Fill() };
        _outputView = new TranscriptView(_outputText) { X = 0, Y = 0, Width = Dim.Fill(), Visible = false };
        _rule = new RuleView { X = 0, Width = Dim.Fill(), Height = 1 };
        _caret = new Label { X = 0, Text = "›", Width = 2, Height = 1 };
        _caret.SetScheme(new Scheme(Styled.Accent));
        _prompt = new PromptView { X = 2, Width = Dim.Fill() };
        _suggest = new SuggestView { X = 0, Width = Dim.Fill(), Visible = false };
        _panel = new PanelView { X = 0, Width = Dim.Fill(), Visible = false };
        _status = new StatusView { X = 0, Width = Dim.Fill(), Height = 1, Y = Pos.AnchorEnd(1) };
        _top.Add(_view, _outputView, _suggest, _rule, _caret, _prompt, _panel, _status);

        _view.Scrolled = () => _rule.Below = _view.Below;
        _view.Wheeled = _outputView.Wheeled = () => _lastKey = DateTime.UtcNow;
        _prompt.Sent += text =>
        {
            _prompt.Remember(text);
            _sent.Writer.TryWrite(text);
            _view.Follow();
        };
        // After the edit has settled; the editor reports a change before its text reflects all of it.
        _prompt.ContentChanged += (_, _) => _app.AddTimeout(TimeSpan.Zero, () =>
        {
            _prompt.Suggest();
            Layout();
            return false;
        });
        _prompt.Completion = _completion;
        _prompt.SuggestionsChanged += () =>
        {
            _suggest.Show(_prompt.Suggestions, _prompt.Selected);
            Layout();
        };
        _transcript.Changed += Redraw;
        _outputText.Changed += () => Ui(_outputView.SetNeedsDraw);
        _app.Keyboard.KeyDown += OnKey;
        _app.Paste += (_, e) =>
        {
            // The editor doesn't take pastes itself; a field in the panel does.
            if (!_prompt.HasFocus)
                return;
            _prompt.Insert(e.Text.ReplaceLineEndings("\n"));
            e.Handled = true;
        };
        _app.ScreenChanged += (_, _) => Layout();
        Layout();
    }

    /// <summary>Builds anchor inside the TUI, runs the REPL, and prints the transcript to the terminal once it closes.</summary>
    public static async Task<int> RunAsync(Options options)
    {
        var exit = 0;
        ExceptionDispatchInfo? failure = null;
        Transcript transcript;
        using (var app = Application.Create())
        {
            app.Init();
            var tui = new Tui(app);
            transcript = tui._transcript;
            Task work = Task.CompletedTask;
            app.AddTimeout(TimeSpan.Zero, () =>
            {
                work = Task.Run(async () =>
                {
                    try
                    {
                        exit = await tui.RunReplAsync(options);
                    }
                    catch (Exception e)
                    {
                        failure = ExceptionDispatchInfo.Capture(e);
                    }
                    finally
                    {
                        app.Invoke(() => app.RequestStop());
                    }
                });
                return false;
            });
            app.Run(tui._top);
            await work;
        }
        Console.Out.Write(transcript.Ansi(color: Environment.GetEnvironmentVariable("NO_COLOR") is null));
        failure?.Throw();
        return exit;
    }

    async Task<int> RunReplAsync(Options options)
    {
        var h = await Startup.BuildAsync(options, new Output(Renderer.Render, this, m => Renderer.Line(Renderer.Warning(m)), Interactive: true));
        await using var _ = h.Mcp;
        _completion.Files = new FileIndex(h.Gate.Workspace).Warm();
        _completion.Servers = () => h.Mcp.Status.Select(s => s.Name);
        _bell = Config.Load().Bell ?? true;
        foreach (var message in h.Agent.History.Where(Messages.IsUserInput))
            Ui(() => _prompt.Remember(message.Text));
        return await new Repl(h.Agent, h.Gate, h.Session, Renderer, this, ReplOptions.From(h, options)).RunAsync();
    }

    public async Task<string?> ReadAsync(CancellationToken ct) => await _sent.Reader.ReadAsync(ct);

    public void Status(string text, bool working) => Ui(() =>
    {
        // After /theme, the views that draw in the theme's colors are drawn again.
        if (_theme != Theme.Current)
        {
            _theme = Theme.Current;
            _caret.SetScheme(new Scheme(Styled.Accent));
            _top.SetNeedsDraw();
        }
        if (_status.Working && !working)
            Ring();
        _status.Set(text, working);
    });

    /// <summary>Whether to ring: the user wants the bell, and hasn't pressed a key for <see cref="Away"/>.</summary>
    internal static bool ShouldRing(bool enabled, DateTime lastKey, DateTime now) => enabled && now - lastKey >= Away;

    // Called on the UI thread, so the bell goes out between frames.
    void Ring()
    {
        if (!ShouldRing(_bell, _lastKey, DateTime.UtcNow))
            return;
        Console.Out.Write('\a');
        Console.Out.Flush();
    }

    // The sequence goes out between frames, so it never lands inside one the driver is writing.
    public string? Copy(string text)
    {
        Ui(() =>
        {
            Console.Out.Write(Clipboard.Osc52(text));
            Console.Out.Flush();
        });
        return Clipboard.CopyNatively(text);
    }

    public void Clear()
    {
        _transcript.Clear();
        Ui(_view.Follow);
    }

    // While a turn streams, what the user sent goes above the line being written; otherwise it simply comes next.
    public void Echo(string text)
    {
        if (_status.Working)
            _transcript.WriteLineAbove(text);
        else
            Renderer.Line(text);
    }

    public async Task<int> ShellAsync(string command, string directory, CancellationToken ct)
    {
        var psi = LineScreen.Shell(command, directory);
        psi.RedirectStandardOutput = psi.RedirectStandardError = psi.RedirectStandardInput = true;
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        process.OutputDataReceived += (_, e) => Show(e.Data);
        process.ErrorDataReceived += (_, e) => Show(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return process.ExitCode;

        void Show(string? line)
        {
            if (line is not null)
                Renderer.Line("  " + line);
        }
    }

    public async Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        Renderer.Line(Renderer.Warning($"  ? {request.Title}"));
        Ui(Ring);
        if (!string.IsNullOrEmpty(request.Detail))
            Renderer.Keep(new ToolOutput(request.Title, request.Detail, Diff: true));
        var hideDiff = string.IsNullOrEmpty(request.Detail) ? null : _transcript.Section(() => Renderer.Diff(request.Detail));
        var key = await KeyAsync(Renderer.AllowPrompt(request.AlwaysLabel), request.AlwaysLabel is null ? "yn" : "yna", ct);
        var answer = char.ToLowerInvariant(key) switch
        {
            'y' => Answer.Yes,
            'a' when request.AlwaysLabel is not null => Answer.Always,
            _ => Answer.No,
        };
        // An allowed change is summed up by the ✎ line that follows; a refused one stays, to say what was refused.
        if (answer != Answer.No)
            hideDiff?.Invoke();
        Renderer.Line(Renderer.Answered(answer));
        return answer;
    }

    public async Task<string?> AskAsync(Question question, CancellationToken ct)
    {
        Renderer.Line(Renderer.Warning($"  ? {question.Text}"));
        Ui(Ring);
        const string other = "Something else (type an answer)";
        var answer = await ChooseAsync(null, question.AllowOther ? [.. question.Options, other] : question.Options, null, false, ct);
        if (answer == other)
            answer = await TextAsync("Your answer:", secret: false, ct);
        Renderer.Line(Renderer.Muted($"  › {answer ?? "(dismissed)"}"));
        return answer;
    }

    // One of a few keys, for approvals; anything else is ignored, so a stray key never answers. Esc counts as no.
    // The prompt brings its own colors.
    Task<char> KeyAsync(string prompt, string keys, CancellationToken ct) =>
        PanelAsync<char>(ct, (panel, done) =>
        {
            panel.Show([(prompt, false)]);
            panel.OnKey = key =>
            {
                if (key == Key.Esc)
                    done('n');
                else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is > 0 and < char.MaxValue and var rune && keys.Contains(char.ToLowerInvariant((char)rune)))
                    done(char.ToLowerInvariant((char)rune));
                return true;
            };
        });

    // The picker from anchor setup, drawn in the panel: ↑/↓, type to filter, Enter, Esc to dismiss (null).
    internal Task<string?> ChooseAsync(string? title, IReadOnlyList<string> choices, string? selected, bool allowTyped, CancellationToken ct) =>
        PanelAsync<string?>(ct, (panel, done) =>
        {
            var picker = new Picker(choices, selected, allowTyped);
            var hint = allowTyped || choices.Count > Picker.DefaultHeight ? "↑/↓ to move, type to filter, Enter to choose" : "↑/↓ to move, Enter to choose";
            Draw();
            panel.OnKey = key =>
            {
                if (ConsoleKeyOf(key) is not { } info)
                    return false;
                switch (picker.Handle(info))
                {
                    case Picker.Outcome.Chosen:
                        done(picker.Result);
                        break;
                    case Picker.Outcome.Cancelled:
                        done(null);
                        break;
                    default:
                        Draw();
                        break;
                }
                return true;
            };

            void Draw() => panel.Show([((title is null ? "" : title + " ") + $"({hint})", false), .. picker.Lines()]);
        });

    // A line of text; secret masks it. Esc dismisses (null).
    internal Task<string?> TextAsync(string prompt, bool secret, CancellationToken ct) =>
        PanelAsync<string?>(ct, (panel, done) =>
        {
            panel.Show([(prompt, true)]);
            var field = new TextField { X = 0, Y = 1, Width = Dim.Fill(), Secret = secret };
            panel.Add(field);
            field.SetFocus();
            field.Accepting += (_, e) =>
            {
                e.Handled = true;
                done(field.Text.Trim() is { Length: > 0 } text ? text : null);
            };
            field.KeyDown += (_, key) =>
            {
                if (key == Key.Esc)
                {
                    key.Handled = true;
                    done(null);
                }
            };
            panel.OnKey = key =>
            {
                if (key != Key.Esc)
                    return false;
                done(null);
                return true;
            };
        }, rows: 2);

    // Shows the panel in place of the prompt (whose draft is kept) until the answer comes, or until ct, which throws.
    // Keys arriving as it appears were meant for the prompt; they're thrown away until typing stops.
    async Task<T> PanelAsync<T>(CancellationToken ct, Action<PanelView, Action<T>> setup, int rows = 0)
    {
        await _panelTurn.WaitAsync(ct);
        try
        {
            var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var _ = ct.Register(() => answer.TrySetCanceled(ct));
            Ui(() =>
            {
                _panel.Reset(DateTime.UtcNow, Quiet);
                setup(_panel, value => answer.TrySetResult(value));
                _panel.Rows = rows;
                _panel.Visible = true;
                _prompt.Visible = _caret.Visible = false;
                Layout();
                if (_panel.SubViews.Count == 0)
                    _panel.SetFocus();
            });
            try
            {
                return await answer.Task;
            }
            finally
            {
                Ui(() =>
                {
                    _panel.Reset(default, Quiet);
                    _panel.Visible = false;
                    _prompt.Visible = _caret.Visible = true;
                    Layout();
                    _prompt.SetFocus();
                });
            }
        }
        finally
        {
            _panelTurn.Release();
        }
    }

    // Keys that mean the same everywhere: Ctrl+C, Ctrl+D, Esc (never quits), scrolling the transcript, and Ctrl+O.
    void OnKey(object? sender, Key key)
    {
        _lastKey = DateTime.UtcNow;
        if (key.KeyCode == (KeyCode.C | KeyCode.CtrlMask))
        {
            key.Handled = true;
            if (Interrupt?.Invoke() == true)
                return;
            if (_output is not null)
                CloseOutput();
            else if (_panel.Visible)
                _panel.Dismiss();
            else if (_prompt.Text.Length > 0)
                _prompt.Text = "";
            else if (DateTime.UtcNow - _lastInterrupt < TimeSpan.FromSeconds(2))
                _sent.Writer.TryWrite(null);
            else
            {
                _lastInterrupt = DateTime.UtcNow;
                _status.Flash("Press Ctrl+C again to exit");
            }
        }
        else if (key.KeyCode == (KeyCode.O | KeyCode.CtrlMask))
        {
            key.Handled = true;
            if (_output is null)
                ShowOutput(int.MaxValue);
            else
                CloseOutput();
        }
        else if (_output is { } shown)
            key.Handled = OutputKey(key, shown);
        else if (key.KeyCode == (KeyCode.D | KeyCode.CtrlMask) && !_panel.Visible && _prompt.Text.Length == 0)
        {
            key.Handled = true;
            _sent.Writer.TryWrite(null);
        }
        else if (key == Key.Esc && !_panel.Visible)
        {
            key.Handled = true;
            _prompt.Dismiss();
        }
        else if (key == Key.PageUp || key == Key.PageDown)
        {
            key.Handled = true;
            _view.Page(key == Key.PageUp ? -1 : 1);
        }
        else if (key.KeyCode == (KeyCode.End | KeyCode.CtrlMask))
        {
            key.Handled = true;
            _view.Follow();
        }
    }

    // While the viewer is open it has the keys, so they scroll it rather than edit the draft or answer an approval
    // behind it. Esc closes it; anything else is ignored.
    bool OutputKey(Key key, int shown)
    {
        if (key == Key.Esc || key == Key.Q)
            CloseOutput();
        else if (key == Key.CursorLeft)
            ShowOutput(shown - 1);
        else if (key == Key.CursorRight)
            ShowOutput(shown + 1);
        else if (key == Key.CursorUp || key == Key.CursorDown)
            _outputView.ScrollBy(key == Key.CursorUp ? -1 : 1);
        else if (key == Key.PageUp || key == Key.PageDown || key == Key.Space)
            _outputView.Page(key == Key.PageUp ? -1 : 1);
        else if (key == Key.Home)
            _outputView.ScrollToTop();
        else if (key == Key.End)
            _outputView.Follow();
        return true;
    }

    // Opens the viewer on output <paramref name="index"/> (clamped, so int.MaxValue is the newest), over the transcript.
    void ShowOutput(int index)
    {
        var outputs = Renderer.Outputs;
        if (outputs.Count == 0)
        {
            _status.Flash("No tool output yet");
            return;
        }
        index = Math.Clamp(index, 0, outputs.Count - 1);
        if (index == _output)
            return;
        _output = index;
        var output = outputs[index];
        _outputText.Clear();
        var renderer = new Renderer(_outputText, color: Environment.GetEnvironmentVariable("NO_COLOR") is null);
        if (output.Diff)
            renderer.Diff(output.Text, int.MaxValue);
        else
            foreach (var line in output.Text.TrimEnd('\n').Split('\n'))
                renderer.Line(line.Replace("\t", "    "));
        _outputView.ScrollToTop();
        _outputView.Visible = true;
        _view.Visible = false;
        _rule.Note = $" {output.Title} · {index + 1} of {outputs.Count} · ←/→ older/newer · Esc to close ";
    }

    void CloseOutput()
    {
        _output = null;
        _outputView.Visible = false;
        _view.Visible = true;
        _rule.Note = null;
        _outputText.Clear();
    }

    // The bottom area grows with the draft (or the panel): rule, prompt or panel, status line.
    void Layout()
    {
        if (_prompt.Viewport.Y > 0 && _prompt.Rows(_app.Screen.Width - 2) <= MaxPromptRows)
            _prompt.Viewport = _prompt.Viewport with { Y = 0 };
        var rows = _panel.Visible ? _panel.Needed() : Math.Clamp(_prompt.Rows(_app.Screen.Width - 2), 1, MaxPromptRows);
        var bottom = rows + 2;
        var suggested = _panel.Visible ? 0 : Math.Min(_prompt.Suggestions.Count, PromptCompletion.MaxShown);
        _suggest.Visible = suggested > 0;
        _suggest.Y = Pos.AnchorEnd(bottom + suggested);
        _suggest.Height = suggested;
        _view.Height = _outputView.Height = Dim.Fill(bottom + suggested);
        _rule.Y = Pos.AnchorEnd(bottom);
        _caret.Y = _prompt.Y = _panel.Y = Pos.AnchorEnd(bottom - 1);
        _prompt.Height = _panel.Height = rows;
        _top.SetNeedsLayout();
    }

    void Redraw()
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 0)
            _app.Invoke(() =>
            {
                _redrawQueued = 0;
                _view.SetNeedsDraw();
            });
    }

    void Ui(Action action) => _app.Invoke(action);

    // The keys Picker understands, as the ConsoleKeyInfo it takes.
    static ConsoleKeyInfo? ConsoleKeyOf(Key key)
    {
        var plain = key.NoShift;
        ConsoleKey? special = plain == Key.Enter ? ConsoleKey.Enter
            : plain == Key.Esc ? ConsoleKey.Escape
            : plain == Key.CursorUp ? ConsoleKey.UpArrow
            : plain == Key.CursorDown ? ConsoleKey.DownArrow
            : plain == Key.PageUp ? ConsoleKey.PageUp
            : plain == Key.PageDown ? ConsoleKey.PageDown
            : plain == Key.Home ? ConsoleKey.Home
            : plain == Key.End ? ConsoleKey.End
            : plain == Key.Backspace ? ConsoleKey.Backspace
            : null;
        if (special is { } k)
            return new ConsoleKeyInfo('\0', k, false, false, false);
        if (key.IsCtrl || key.IsAlt || key.AsRune.Value is <= 0 or > char.MaxValue)
            return null;
        return new ConsoleKeyInfo((char)key.AsRune.Value, ConsoleKey.NoName, key.IsShift, false, false);
    }

    /// <summary>anchor setup's questions, asked in the panel. Setup runs on the REPL's task, so blocking here is safe.</summary>
    sealed class TuiSetupIO(Tui tui) : ISetupIO
    {
        public string? Ask(string prompt) => tui.TextAsync(prompt.Trim(), false, CancellationToken.None).GetAwaiter().GetResult();

        public string? AskSecret(string prompt) => tui.TextAsync(prompt.Trim(), true, CancellationToken.None).GetAwaiter().GetResult();

        public string? Select(string title, IReadOnlyList<string> choices, string? selected = null, bool allowTyped = false)
        {
            tui.Renderer.Line(title);
            var answer = tui.ChooseAsync(null, choices, selected, allowTyped, CancellationToken.None).GetAwaiter().GetResult();
            tui.Renderer.Line(tui.Renderer.Muted($"  › {answer ?? "(cancelled)"}"));
            return answer;
        }

        public void Line(string text = "") => tui.Renderer.Line(text);

        public void Note(string text, bool? ok = null) => SetupNotes.Write(tui.Renderer, Line, text, ok);
    }
}

/// <summary>The transcript, word-wrapped to the view's width. It follows new output unless the user has scrolled up.</summary>
sealed class TranscriptView(Transcript transcript) : View
{
    readonly List<List<Span>> _rows = [];
    readonly List<int> _starts = []; // the row each transcript line starts on
    int _width = -1;
    int _version = -1;
    int _removals;
    int _top;
    bool _follow = true;
    int _reported;

    /// <summary>Raised when <see cref="Below"/> changes: on a scroll, or when output arrives or goes while scrolled up.</summary>
    public Action? Scrolled { get; set; }

    /// <summary>Raised when the user scrolls with the mouse.</summary>
    public Action? Wheeled { get; set; }

    /// <summary>Rows below the bottom of the view, when the user has scrolled up.</summary>
    public int Below => _follow ? 0 : Math.Max(0, _rows.Count - _top - Viewport.Height);

    public void Page(int direction) => ScrollBy(direction * Math.Max(1, Viewport.Height - 2));

    public void ScrollToTop()
    {
        _follow = false;
        _top = 0;
        SetNeedsDraw();
        Scrolled?.Invoke();
    }

    public void Follow()
    {
        _follow = true;
        SetNeedsDraw();
        Scrolled?.Invoke();
    }

    public void ScrollBy(int rows)
    {
        Sync();
        var last = Math.Max(0, _rows.Count - Viewport.Height);
        _top = Math.Clamp((_follow ? last : _top) + rows, 0, last);
        _follow = _top >= last;
        SetNeedsDraw();
        Scrolled?.Invoke();
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
            ScrollBy(-3);
        else if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
            ScrollBy(3);
        else
            return false;
        Wheeled?.Invoke();
        return true;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Sync();
        var height = Viewport.Height;
        // Following, or the transcript shrank (a hidden diff) under where the user had scrolled to.
        _top = _follow ? Math.Max(0, _rows.Count - height) : Math.Min(_top, Math.Max(0, _rows.Count - height));
        for (var i = 0; i < height; i++)
        {
            Move(0, i);
            var used = 0;
            if (_top + i < _rows.Count)
                foreach (var span in _rows[_top + i])
                {
                    SetAttribute(Styled.Of(span.Style, Styled.Plain));
                    AddStr(span.Text);
                    used += span.Text.GetColumns();
                }
            SetAttribute(new Attribute(Color.None, Color.None));
            if (used < Viewport.Width)
                AddStr(new string(' ', Viewport.Width - used));
        }
        if (Below != _reported)
        {
            _reported = Below;
            Scrolled?.Invoke();
        }
        return true;
    }

    // Rewraps everything when the width changes or lines were taken out; otherwise only from the first line that may
    // have changed: the last one (which may have grown), or the start of a streamed message that was redrawn.
    void Sync()
    {
        var width = Viewport.Width;
        if (width == _width && transcript.Version == _version && transcript.Removals == _removals)
            return;
        if (width != _width || transcript.Removals != _removals)
        {
            _width = width;
            _removals = transcript.Removals;
            _rows.Clear();
            _starts.Clear();
        }
        var (version, from, lines) = transcript.Since(_starts.Count);
        _version = version;
        if (from < _starts.Count)
        {
            _rows.RemoveRange(_starts[from], _rows.Count - _starts[from]);
            _starts.RemoveRange(from, _starts.Count - from);
        }
        foreach (var line in lines)
        {
            _starts.Add(_rows.Count);
            _rows.AddRange(Transcript.Wrap(line, width));
        }
    }
}

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

/// <summary>
/// The message being written: Enter sends, Alt+Enter or Shift+Enter starts a new line, ↑/↓ at the edges walk history.
/// While it suggests completions (a slash command, an @ path), ↑/↓ choose one and Tab or Enter takes it.
/// </summary>
sealed class PromptView : Editor
{
    readonly List<string> _history = [];
    int _index;
    string _draft = "";
    int _start; // where the text the suggestions would replace begins
    string? _dismissed; // the draft as it was when Esc hid the suggestions; they stay hidden until it changes

    public PromptCompletion? Completion { get; set; }

    public IReadOnlyList<Suggestion> Suggestions { get; private set; } = [];

    public int Selected { get; private set; }

    public event Action? SuggestionsChanged;

    /// <summary>Works out the suggestions for the text before the caret.</summary>
    public void Suggest()
    {
        var typed = Document is null ? "" : Text[..Math.Min(CaretOffset, Text.Length)];
        var (start, items) = typed == _dismissed || Completion is null ? (0, []) : Completion.Suggest(typed);
        if (items.SequenceEqual(Suggestions) && start == _start)
            return;
        (_start, Suggestions, Selected) = (start, items, 0);
        SuggestionsChanged?.Invoke();
    }

    public void Dismiss()
    {
        if (Suggestions.Count == 0)
            return;
        _dismissed = Text[..Math.Min(CaretOffset, Text.Length)];
        Suggest();
    }

    // Replaces what's typed from where the suggestion starts up to the caret.
    void Accept()
    {
        var item = Suggestions[Selected].Text;
        Document!.Replace(_start, CaretOffset - _start, item);
        CaretOffset = _start + item.Length;
        Suggest();
    }

    public PromptView()
    {
        Multiline = true;
        WordWrap = true;
        GutterOptions = GutterOptions.None;
    }

    public event Action<string>? Sent;

    public void Remember(string text)
    {
        if (text.Length > 0 && (_history.Count == 0 || _history[^1] != text))
            _history.Add(text);
        _index = _history.Count;
    }

    /// <summary>Rows the draft takes when wrapped to <paramref name="width"/>.</summary>
    public int Rows(int width) =>
        Text.Split('\n').Sum(line => Math.Max(1, (line.GetColumns() + Math.Max(1, width) - 1) / Math.Max(1, width)));

    protected override bool OnKeyDown(Key key)
    {
        // Typing that came just before this key may not have been looked at yet.
        if (Suggestions.Count > 0)
            Suggest();
        if (Suggestions.Count > 0)
        {
            if (key == Key.CursorUp || key == Key.CursorDown)
            {
                Selected = (Selected + (key == Key.CursorUp ? -1 : 1) + Suggestions.Count) % Suggestions.Count;
                SuggestionsChanged?.Invoke();
                return true;
            }
            if (key == Key.Tab || key.KeyCode == KeyCode.Enter)
            {
                Accept();
                return true;
            }
        }
        var handled = Handle(key);
        Suggest(); // the caret may have moved without the text changing
        return handled;
    }

    bool Handle(Key key)
    {
        if (key.KeyCode == KeyCode.Enter)
        {
            var text = Text.Trim();
            Text = "";
            _draft = "";
            if (text.Length > 0)
                Sent?.Invoke(text);
            return true;
        }
        if (Document is not { } document)
            return base.OnKeyDown(key);
        // Alt+Enter often arrives as Ctrl+Alt+M, since Enter is a carriage return.
        if (key.KeyCode is (KeyCode.Enter | KeyCode.ShiftMask) or (KeyCode.Enter | KeyCode.AltMask) or (KeyCode.M | KeyCode.CtrlMask | KeyCode.AltMask))
        {
            Insert("\n");
            return true;
        }
        var line = document.GetLineByOffset(CaretOffset).LineNumber;
        if (key.KeyCode == (KeyCode.U | KeyCode.CtrlMask))
        {
            var start = document.GetLineByOffset(CaretOffset).Offset;
            document.Remove(start, CaretOffset - start);
            return true;
        }
        if (key == Key.CursorUp && line == 1 && _index > 0)
            return Recall(_index - 1);
        if (key == Key.CursorDown && line == document.LineCount && _index < _history.Count)
            return Recall(_index + 1);
        return base.OnKeyDown(key);
    }

    public void Insert(string text)
    {
        var at = CaretOffset;
        Document?.Insert(at, text);
        CaretOffset = at + text.Length;
    }

    bool Recall(int index)
    {
        if (_index == _history.Count)
            _draft = Text;
        _index = index;
        Text = index == _history.Count ? _draft : _history[index];
        CaretOffset = Text.Length;
        return true;
    }
}

/// <summary>A completion the prompt offers: the text it puts in, and a few words on what it is.</summary>
sealed record Suggestion(string Text, string Note = "");

/// <summary>
/// Completes slash commands while the first word of the draft is one, then the words some of them take (a theme, an
/// MCP server), and workspace paths after an @.
/// </summary>
sealed class PromptCompletion
{
    /// <summary>Rows the list takes at most; it scrolls to keep the chosen one in view.</summary>
    public const int MaxShown = 8;

    public FileIndex? Files { get; set; }

    /// <summary>The MCP servers' names, for /mcp login and logout.</summary>
    public Func<IEnumerable<string>>? Servers { get; set; }

    /// <summary>
    /// What could replace the end of <paramref name="typed"/> (the draft up to the caret): the offset the replaced
    /// text starts at, and the replacements, "/help" for "/he", "mono" for "/theme m", or "@src/" for "@s".
    /// </summary>
    public (int Start, List<Suggestion> Items) Suggest(string typed)
    {
        if (typed.StartsWith('/') && !typed.Contains('\n'))
        {
            var space = typed.IndexOf(' ');
            if (space < 0)
                return (0, [.. Repl.Help.Select(h => new Suggestion(h.Usage.Split(' ')[0], h.Description))
                    .Where(c => c.Text.StartsWith(typed, StringComparison.Ordinal) && c.Text != typed)]);
            var words = typed[(space + 1)..].Split(' ');
            var word = words[^1];
            return (typed.Length - word.Length, [.. Arguments(typed[..space], words)
                .Where(a => a.Text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && a.Text != word)]);
        }
        var start = typed.LastIndexOfAny([' ', '\n', '\t']) + 1;
        if (Files is null || start >= typed.Length || typed[start] != '@')
            return (0, []);
        return (start, [.. Files.Complete(typed[(start + 1)..]).Select(p => new Suggestion("@" + p))]);
    }

    // What the word being typed after a command can be; words[^1] is that word, the ones before it are already typed.
    IEnumerable<Suggestion> Arguments(string command, string[] words) => (command, words.Length) switch
    {
        ("/theme", 1) => Theme.BuiltIn.Select(t => new Suggestion(t.Name, t.Description)),
        ("/mcp", 1) => [new("login", "sign in to a server"), new("logout", "sign out of a server")],
        ("/mcp", 2) when words[0] is "login" or "logout" => (Servers?.Invoke() ?? []).Select(s => new Suggestion(s)),
        ("/until", 1) => [new("off", "stop checking")],
        ("/approvals", 1) => [new("clear", "forget every saved \"always\" answer")],
        ("/copy", 1) => [new("code", "a code block from the last reply")],
        _ => [],
    };
}

/// <summary>The prompt's suggestions, above it, the chosen one picked out.</summary>
sealed class SuggestView : View
{
    IReadOnlyList<Suggestion> _items = [];
    int _selected;
    int _top;

    public void Show(IReadOnlyList<Suggestion> items, int selected)
    {
        if (!ReferenceEquals(items, _items))
            _top = 0;
        (_items, _selected) = (items, selected);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = _items.Count == 0 ? 0 : _items.Max(s => s.Text.GetColumns());
        var muted = Theme.StyleOf(Theme.Current.Muted);
        if (Viewport.Height <= 0)
            return true;
        _top = Math.Clamp(_top, Math.Max(0, _selected - Viewport.Height + 1), _selected);
        for (var row = 0; row < Viewport.Height; row++)
        {
            var i = _top + row;
            if (i >= _items.Count)
            {
                Styled.Draw(this, row, [], Styled.Plain);
                continue;
            }
            var (item, chosen) = (_items[i], i == _selected);
            var note = item.Note.Length == 0 ? "" : new string(' ', width - item.Text.GetColumns() + 3) + item.Note;
            Styled.Draw(this, row, [new((chosen ? "› " : "  ") + item.Text, chosen ? default : muted), new(note, muted),
                new(chosen ? "   Tab to complete" : "", muted)], chosen ? Styled.Accent : Styled.Plain);
        }
        return true;
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

    public void Flash(string message)
    {
        _flash = message;
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
