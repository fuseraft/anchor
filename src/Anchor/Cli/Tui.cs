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
public sealed partial class Tui : IReplScreen, IApprover
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

    static bool Colored => Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static bool Supported => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public Renderer Renderer { get; }

    public bool FullScreen => true;

    public Func<bool>? Interrupt { private get; set; }

    public ISetupIO SetupIO { get; }

    public bool CanAsk => true;

    Tui(IApplication app)
    {
        _app = app;
        Renderer = new Renderer(_transcript, color: Colored);
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
        Console.Out.Write(transcript.Ansi(color: Colored));
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

    // Keys that mean the same everywhere: Ctrl+C, Ctrl+D, Esc (never quits), scrolling the transcript, and Ctrl+O.
    void OnKey(object? sender, Key key)
    {
        _lastKey = DateTime.UtcNow;
        var ctrlC = key.KeyCode == (KeyCode.C | KeyCode.CtrlMask);
        if (!ctrlC)
        {
            // Any other key disarms a pending Ctrl+C exit, and a hint shown for the last key no longer applies.
            _lastInterrupt = default;
            _status.ClearFlash();
        }
        key.Handled = ctrlC ? Run(CtrlC)
            : key.KeyCode == (KeyCode.O | KeyCode.CtrlMask) ? Run(ToggleOutput)
            : _find is { } find ? FindKey(key, find)
            : _output is { } shown ? OutputKey(key, shown)
            : key.KeyCode == (KeyCode.F | KeyCode.CtrlMask) ? Run(OpenFind)
            : key.KeyCode == (KeyCode.Home | KeyCode.CtrlMask) ? Run(_view.ScrollToTop)
            : key.KeyCode == (KeyCode.R | KeyCode.CtrlMask) && !_panel.Visible ? Run(() => _ = SearchHistoryAsync())
            : key.KeyCode == (KeyCode.D | KeyCode.CtrlMask) && !_panel.Visible && _prompt.Text.Length == 0 ? Run(() => _sent.Writer.TryWrite(null))
            : key == Key.Esc && !_panel.Visible ? Run(_prompt.Dismiss)
            : key == Key.PageUp || key == Key.PageDown ? Run(() => _view.Page(key == Key.PageUp ? -1 : 1))
            : key.KeyCode == (KeyCode.End | KeyCode.CtrlMask) ? Run(_view.Follow)
            : key.Handled;

        static bool Run(Action action)
        {
            action();
            return true;
        }
    }

    // Ctrl+C cancels the running turn, or else closes or clears what's open; twice in a row on an empty prompt exits.
    void CtrlC()
    {
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
}
