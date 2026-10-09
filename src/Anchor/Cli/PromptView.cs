using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

namespace Anchor.Cli;

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

    /// <summary>Messages sent so far, oldest first.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>Puts <paramref name="text"/> in place of the draft, the caret at its end.</summary>
    public void Replace(string text)
    {
        Text = text;
        CaretOffset = text.Length;
        _index = _history.Count;
    }

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
