using System.Text;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Anchor.Cli;

/// <summary>
/// Colors the lines of a code block with VS Code's TextMate grammars (TextMateSharp, which Terminal.Gui already brings).
/// Kinds of token map to the terminal's own colors, not a theme's, so code reads on light and dark backgrounds alike.
/// </summary>
public sealed class Highlighter
{
    static readonly Lock Gate = new();
    static RegistryOptions? _options;
    static Registry? _registry;
    static bool _broken;

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c#"] = "cs", ["shell"] = "sh", ["zsh"] = "sh", ["console"] = "sh", ["shellsession"] = "sh", ["golang"] = "go",
    };

    // The first rule a token's innermost scope starts with decides its color.
    static readonly (string Scope, string Sgr)[] Colors =
    [
        ("comment", "2"),
        ("string", "32"),
        ("constant.character.escape", "33"),
        ("constant", "33"),
        ("keyword.operator", ""),
        ("keyword", "35"),
        ("storage", "35"),
        ("entity.name.function", "94"),
        ("support.function", "94"),
        ("entity.name.tag", "94"),
        ("entity.name.variable", ""),
        ("entity.name", "36"),
        ("support.type", "36"),
        ("support.class", "36"),
        ("entity.other.attribute-name", "36"),
        ("markup.inserted", "32"),
        ("markup.deleted", "31"),
        ("meta.diff.header", "1"),
        ("markup.heading", "1"),
    ];

    readonly IGrammar _grammar;
    IStateStack? _state;

    Highlighter(IGrammar grammar) => _grammar = grammar;

    /// <summary>A highlighter for one code block in <paramref name="language"/>, or null for a language it doesn't know.</summary>
    public static Highlighter? For(string language)
    {
        if (language.Length == 0)
            return null;
        language = Aliases.GetValueOrDefault(language, language);
        lock (Gate)
        {
            if (_broken)
                return null;
            try
            {
                _options ??= new RegistryOptions(ThemeName.DarkPlus);
                _registry ??= new Registry(_options);
                var scope = _options.GetScopeByLanguageId(language.ToLowerInvariant()) ?? _options.GetScopeByExtension("." + language.ToLowerInvariant());
                return scope is null || _registry.LoadGrammar(scope) is not { } grammar ? null : new Highlighter(grammar);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The grammars need a native regex library; without it, code blocks are simply not colored.
                _broken = true;
                return null;
            }
        }
    }

    /// <summary>
    /// The next line of the block as ANSI text. Lines must come in order, since a token (a comment, a string) can span
    /// several; one still being written is colored without <paramref name="advance"/>, so it can be colored again.
    /// </summary>
    public string Line(string line, bool advance = true)
    {
        ITokenizeLineResult result;
        try
        {
            lock (Gate)
                result = _grammar.TokenizeLine(line, _state, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return line;
        }
        if (advance)
            _state = result.RuleStack;
        var sb = new StringBuilder();
        foreach (var token in result.Tokens)
        {
            var start = Math.Min(token.StartIndex, line.Length);
            var text = line[start..Math.Min(token.EndIndex, line.Length)];
            var sgr = Sgr(token.Scopes);
            sb.Append(sgr.Length == 0 || text.Length == 0 ? text : $"\e[{sgr}m{text}\e[0m");
        }
        return sb.ToString();
    }

    static string Sgr(List<string> scopes)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
            foreach (var (scope, sgr) in Colors)
                if (scopes[i].StartsWith(scope, StringComparison.Ordinal))
                    return sgr;
        return "";
    }
}
