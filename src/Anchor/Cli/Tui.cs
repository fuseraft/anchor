using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Anchor.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
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
    string? _find; // what Ctrl+F is looking for, while it's open
    int? _found; // the transcript line of the match it's on
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
        Renderer.Clear();
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
        var hideDiff = string.IsNullOrEmpty(request.Detail) ? null : Renderer.Section(() => Renderer.Diff(request.Detail));
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
    internal Task<string?> ChooseAsync(string? title, IReadOnlyList<string> choices, string? selected, bool allowTyped, CancellationToken ct, bool quiet = true) =>
        PanelAsync<string?>(ct, quiet: quiet, setup: (panel, done) =>
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
    // Keys arriving as it appears were meant for the prompt; they're thrown away until typing stops, unless the user
    // opened the panel (not quiet), when they're meant for it.
    async Task<T> PanelAsync<T>(CancellationToken ct, Action<PanelView, Action<T>> setup, int rows = 0, bool quiet = true)
    {
        await _panelTurn.WaitAsync(ct);
        try
        {
            var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var _ = ct.Register(() => answer.TrySetCanceled(ct));
            Ui(() =>
            {
                _panel.Reset(quiet ? DateTime.UtcNow : default, Quiet);
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
        if (key.KeyCode != (KeyCode.C | KeyCode.CtrlMask))
        {
            // Any other key disarms a pending Ctrl+C exit, and a hint shown for the last key no longer applies.
            _lastInterrupt = default;
            _status.ClearFlash();
        }
        if (key.KeyCode == (KeyCode.C | KeyCode.CtrlMask))
        {
            key.Handled = true;
            if (Interrupt?.Invoke() == true)
                return;
            if (_find is not null)
                CloseFind();
            else if (_output is not null)
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
                _status.Flash("Press Ctrl+C again to exit", TimeSpan.FromSeconds(2));
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
        else if (_find is { } find)
            key.Handled = FindKey(key, find);
        else if (_output is { } shown)
            key.Handled = OutputKey(key, shown);
        else if (key.KeyCode == (KeyCode.F | KeyCode.CtrlMask))
        {
            key.Handled = true;
            _find = "";
            Find(older: null);
        }
        else if (key.KeyCode == (KeyCode.Home | KeyCode.CtrlMask))
        {
            key.Handled = true;
            _view.ScrollToTop();
        }
        else if (key.KeyCode == (KeyCode.R | KeyCode.CtrlMask) && !_panel.Visible)
        {
            key.Handled = true;
            _ = SearchHistoryAsync();
        }
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

    // While Ctrl+F is open it has the keys: typing narrows the search, Enter or ↑ goes to an older match and ↓ to a
    // newer one, and Esc closes it where it is. PgUp/PgDn still scroll.
    bool FindKey(Key key, string find)
    {
        if (key == Key.Esc)
            CloseFind();
        else if (key.KeyCode == KeyCode.Enter || key == Key.CursorUp || key.KeyCode == (KeyCode.F | KeyCode.CtrlMask))
            Find(older: true);
        else if (key == Key.CursorDown)
            Find(older: false);
        else if (key == Key.PageUp || key == Key.PageDown)
            _view.Page(key == Key.PageUp ? -1 : 1);
        else if (key == Key.Backspace)
        {
            if (find.Length > 0)
                (_find, _found) = (find[..^1], null);
            Find(older: null);
        }
        else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is >= ' ' and var rune)
        {
            (_find, _found) = (find + char.ConvertFromUtf32(rune), null);
            Find(older: null);
        }
        return true;
    }

    // Moves to the next match older or newer than the one it's on; null starts again from the newest.
    void Find(bool? older)
    {
        var find = _find ?? "";
        var matches = find.Length == 0 ? [] : Matches(_transcript.Lines(), find);
        if (matches.Count > 0)
        {
            var at = _found is { } found ? matches.IndexOf(found) : -1;
            at = older switch
            {
                null => matches.Count - 1,
                _ when at < 0 => matches.Count - 1,
                true => Math.Max(0, at - 1),
                false => Math.Min(matches.Count - 1, at + 1),
            };
            _found = matches[at];
            _view.ShowLine(matches[at]);
            _rule.Note = $" find: {find} · {at + 1} of {matches.Count} · Enter older · ↓ newer · Esc to close ";
        }
        else
            _rule.Note = find.Length == 0 ? " find: type to search the conversation · Esc to close " : $" find: {find} · no matches · Esc to close ";
        _view.Highlight = find.Length == 0 ? null : find;
    }

    /// <summary>The transcript lines holding <paramref name="find"/>, ignoring case, oldest first.</summary>
    internal static List<int> Matches(List<List<Span>> lines, string find) =>
        [.. lines.Select((line, i) => (Text: string.Concat(line.Select(s => s.Text)), i))
            .Where(l => l.Text.Contains(find, StringComparison.OrdinalIgnoreCase)).Select(l => l.i)];

    void CloseFind()
    {
        (_find, _found) = (null, null);
        _view.Highlight = null;
        _rule.Note = null;
    }

    // Ctrl+R: earlier messages, newest first, filtered as the user types; the chosen one replaces the draft. A
    // multi-line message is shown on one line, and matched on all of it.
    async Task SearchHistoryAsync()
    {
        Dictionary<string, string> messages = [];
        foreach (var message in _prompt.History.Reverse())
            messages.TryAdd(message.ReplaceLineEndings(" ⏎ "), message);
        if (messages.Count == 0)
        {
            _status.Flash("No messages to search yet");
            return;
        }
        if (await ChooseAsync("History, newest first; type to search", [.. messages.Keys], null, false, CancellationToken.None, quiet: false) is { } chosen)
            Ui(() => _prompt.Replace(messages[chosen]));
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

    /// <summary>anchor setup's questions, asked in the panel.</summary>
    sealed class TuiSetupIO(Tui tui) : ISetupIO
    {
        public Task<string?> AskAsync(string prompt, CancellationToken ct = default) => tui.TextAsync(prompt.Trim(), false, ct);

        public Task<string?> AskSecretAsync(string prompt, CancellationToken ct = default) => tui.TextAsync(prompt.Trim(), true, ct);

        public async Task<string?> SelectAsync(string title, IReadOnlyList<string> choices, string? selected = null, bool allowTyped = false, CancellationToken ct = default)
        {
            tui.Renderer.Line(title);
            var answer = await tui.ChooseAsync(null, choices, selected, allowTyped, ct);
            tui.Renderer.Line(tui.Renderer.Muted($"  › {answer ?? "(cancelled)"}"));
            return answer;
        }

        public void Line(string text = "") => tui.Renderer.Line(text);

        public void Note(string text, bool? ok = null) => SetupNotes.Write(tui.Renderer, Line, text, ok);
    }
}
