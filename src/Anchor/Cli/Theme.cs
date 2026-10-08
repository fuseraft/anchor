namespace Anchor.Cli;

/// <summary>
/// The colors anchor draws with, each an SGR code ("36", "1;94") for one role. Colors are the terminal's own 16, so a
/// theme follows the terminal's palette and reads on light and dark backgrounds alike.
/// </summary>
public sealed record Theme
{
    public string Name { get; init; } = "default";

    public string Description { get; init; } = "anchor's usual colors";

    /// <summary>anchor's own color: the caret, the title, the spinner, the picker's choice.</summary>
    public string Accent { get; init; } = "36";

    /// <summary>A tool call's name. Bright blue: plain blue is too dark to read on most dark terminal themes.</summary>
    public string Tool { get; init; } = "94";

    /// <summary>A sub-agent's tag.</summary>
    public string Agent { get; init; } = "35";

    public string Success { get; init; } = "32";

    public string Warning { get; init; } = "33";

    public string Error { get; init; } = "31";

    /// <summary>What's there but not the point: tool arguments, token counts, hints.</summary>
    public string Muted { get; init; } = "2";

    /// <summary>The line above the prompt and the status line.</summary>
    public string Border { get; init; } = "90";

    public string Heading { get; init; } = "1";

    public string Link { get; init; } = "4";

    /// <summary>Inline code, and code blocks in a language the highlighter doesn't know.</summary>
    public string Code { get; init; } = "36";

    public string Comment { get; init; } = "2";

    public string String { get; init; } = "32";

    public string Constant { get; init; } = "33";

    public string Keyword { get; init; } = "35";

    public string Function { get; init; } = "94";

    public string Type { get; init; } = "36";

    /// <summary>The theme everything draws with; set once at startup, and by /theme.</summary>
    public static Theme Current { get; set; } = new();

    public static readonly IReadOnlyList<Theme> BuiltIn =
    [
        new(),
        // For terminals whose normal colors are too dark: the bright variants, and nothing dim.
        new()
        {
            Name = "bright", Description = "bright colors, for dark terminals whose normal colors are too dark",
            Accent = "96", Tool = "94", Agent = "95", Success = "92", Warning = "93", Error = "91",
            Muted = "37", Code = "96", Comment = "37", String = "92", Constant = "93", Keyword = "95", Function = "94", Type = "96",
        },
        // For light backgrounds: no yellow and no bright variants, which wash out on white. Warnings are magenta instead.
        new()
        {
            Name = "light", Description = "for light backgrounds: no yellow or bright colors",
            Accent = "34", Tool = "34", Agent = "36", Success = "32", Warning = "35", Error = "31",
            Code = "34", Comment = "2", String = "32", Constant = "31", Keyword = "35", Function = "34", Type = "36",
        },
        // No colors at all, only bold, dim and underline.
        new()
        {
            Name = "mono", Description = "no colors, only bold, dim and underline",
            Accent = "1", Tool = "1", Agent = "1", Success = "", Warning = "1", Error = "1", Border = "2",
            Code = "", Comment = "2", String = "", Constant = "", Keyword = "1", Function = "", Type = "",
        },
    ];

    /// <summary>The theme a config names, with its colors; <paramref name="name"/> (from --theme) overrides it.</summary>
    public static Theme Of(Config config, string? name = null) => From(name ?? config.Theme, config.Colors).Theme;

    public static Theme? Named(string name) => BuiltIn.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static readonly Dictionary<string, Func<Theme, string, Theme>> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accent"] = (t, v) => t with { Accent = v },
        ["tool"] = (t, v) => t with { Tool = v },
        ["agent"] = (t, v) => t with { Agent = v },
        ["success"] = (t, v) => t with { Success = v },
        ["warning"] = (t, v) => t with { Warning = v },
        ["error"] = (t, v) => t with { Error = v },
        ["muted"] = (t, v) => t with { Muted = v },
        ["border"] = (t, v) => t with { Border = v },
        ["heading"] = (t, v) => t with { Heading = v },
        ["link"] = (t, v) => t with { Link = v },
        ["code"] = (t, v) => t with { Code = v },
        ["comment"] = (t, v) => t with { Comment = v },
        ["string"] = (t, v) => t with { String = v },
        ["constant"] = (t, v) => t with { Constant = v },
        ["keyword"] = (t, v) => t with { Keyword = v },
        ["function"] = (t, v) => t with { Function = v },
        ["type"] = (t, v) => t with { Type = v },
    };

    static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "30", ["red"] = "31", ["green"] = "32", ["yellow"] = "33", ["blue"] = "34", ["magenta"] = "35", ["cyan"] = "36", ["white"] = "37",
        ["gray"] = "90", ["grey"] = "90", ["bright-red"] = "91", ["bright-green"] = "92", ["bright-yellow"] = "93", ["bright-blue"] = "94",
        ["bright-magenta"] = "95", ["bright-cyan"] = "96", ["bright-white"] = "97",
        ["bold"] = "1", ["dim"] = "2", ["italic"] = "3", ["underline"] = "4",
    };

    /// <summary>
    /// The theme a config asks for: a built-in one by <paramref name="name"/>, with <paramref name="colors"/> mapping
    /// roles to words such as "bold magenta" (or "none") on top. Anything not understood is reported and left as it was.
    /// </summary>
    public static (Theme Theme, List<string> Warnings) From(string? name, IReadOnlyDictionary<string, string>? colors)
    {
        List<string> warnings = [];
        var theme = BuiltIn[0];
        if (name is not null)
        {
            if (Named(name) is { } named)
                theme = named;
            else
                warnings.Add($"Unknown theme '{name}'; using the default. Themes: {string.Join(", ", BuiltIn.Select(t => t.Name))}.");
        }
        foreach (var (role, value) in colors ?? new Dictionary<string, string>())
        {
            if (!Roles.TryGetValue(role, out var set))
                warnings.Add($"Unknown color role '{role}'. Roles: {string.Join(", ", Roles.Keys)}.");
            else if (Sgr(value) is { } sgr)
                theme = set(theme, sgr);
            else
                warnings.Add($"Can't read the color '{value}' for '{role}'. Use words like red, bright-blue, gray, bold, dim, italic, underline, or none.");
        }
        return (theme, warnings);
    }

    /// <summary>Color words ("bold bright-cyan") as an SGR code, "" for "none", or null when a word isn't one.</summary>
    public static string? Sgr(string words)
    {
        List<string> codes = [];
        foreach (var word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("none", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Words.TryGetValue(word, out var code))
                return null;
            codes.Add(code);
        }
        return string.Join(';', codes);
    }

    /// <summary>A role's code as a transcript style, for what the TUI draws itself.</summary>
    public static Style StyleOf(string sgr)
    {
        var transcript = new Transcript();
        transcript.Write($"\e[{sgr}mx");
        return transcript.Lines()[0][0].Style;
    }
}
