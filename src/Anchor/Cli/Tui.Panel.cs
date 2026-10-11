using Anchor.Core;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Anchor.Cli;

// Approvals, ask_user and setup's questions, asked in the panel that takes the prompt's place.
public sealed partial class Tui
{
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
