using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Anchor.Core;

public sealed record Skill(string Name, string Description, string Directory);

public sealed record AgentDefinition(string Name, string Description, IReadOnlyList<string>? Tools, string? Model, string Prompt);

/// <summary>Skills (<c>&lt;root&gt;/&lt;name&gt;/SKILL.md</c>) and sub-agents (<c>&lt;root&gt;/&lt;name&gt;.md</c>): Markdown with YAML frontmatter.</summary>
public static partial class Definitions
{
    static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>Loads skills from <paramref name="roots"/> in precedence order; an earlier root wins a name clash.</summary>
    public static (List<Skill> Skills, List<string> Warnings) LoadSkills(IEnumerable<string> roots)
    {
        var skills = new Dictionary<string, Skill>();
        var warnings = new List<string>();
        foreach (var dir in roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateDirectories(r).Order(StringComparer.Ordinal)))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file) || LinksToSecret(file))
                continue;
            try
            {
                var (fields, _) = Frontmatter(File.ReadAllText(file));
                var name = Required(fields, "name");
                if (name != Path.GetFileName(dir))
                    throw new FormatException($"name '{name}' must match its directory name");
                skills.TryAdd(name, new Skill(ValidName(name), Description(fields), dir));
            }
            catch (FormatException e)
            {
                warnings.Add($"Skipped skill {file}: {e.Message}.");
            }
        }
        return ([.. skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal)], warnings);
    }

    /// <summary>Loads sub-agent definitions from <paramref name="roots"/> in precedence order.</summary>
    public static (List<AgentDefinition> Agents, List<string> Warnings) LoadAgents(IEnumerable<string> roots)
    {
        var agents = new Dictionary<string, AgentDefinition>();
        var warnings = new List<string>();
        foreach (var file in roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*.md").Order(StringComparer.Ordinal)))
        {
            if (LinksToSecret(file))
                continue;
            try
            {
                var (fields, body) = Frontmatter(File.ReadAllText(file));
                var name = ValidName(Required(fields, "name"));
                var tools = fields.GetValueOrDefault("tools") switch
                {
                    null => null,
                    string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    List<object> list => list.Select(t => t.ToString()!).ToArray(),
                    _ => throw new FormatException("tools must be a list or a comma-separated string"),
                };
                agents.TryAdd(name, new AgentDefinition(name, Description(fields), tools, fields.GetValueOrDefault("model") as string, body.Trim()));
            }
            catch (FormatException e)
            {
                warnings.Add($"Skipped agent {file}: {e.Message}.");
            }
        }
        return ([.. agents.Values.OrderBy(a => a.Name, StringComparer.Ordinal)], warnings);
    }

    /// <summary>Splits a document into its YAML frontmatter fields and Markdown body.</summary>
    public static (Dictionary<string, object?> Fields, string Body) Frontmatter(string text)
    {
        text = text.ReplaceLineEndings("\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new FormatException("missing YAML frontmatter (--- at the top)");
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            throw new FormatException("unterminated YAML frontmatter");
        try
        {
            var fields = Yaml.Deserialize<Dictionary<string, object?>>(text[4..end]) ?? [];
            var bodyStart = text.IndexOf('\n', end + 4);
            return (fields, bodyStart < 0 ? "" : text[(bodyStart + 1)..]);
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new FormatException($"invalid YAML frontmatter: {e.Message}");
        }
    }

    static bool LinksToSecret(string file) => Secrets.IsSecretPath(Workspace.RealPath(Path.GetFullPath(file)));

    static string Required(Dictionary<string, object?> fields, string key) =>
        fields.GetValueOrDefault(key) is string { Length: > 0 } value ? value.Trim() : throw new FormatException($"'{key}' is required");

    static string Description(Dictionary<string, object?> fields)
    {
        var description = Required(fields, "description");
        return description.Length <= 1024 ? description : throw new FormatException("description is longer than 1024 characters");
    }

    static string ValidName(string name) =>
        name.Length <= 64 && NamePattern().IsMatch(name)
            ? name
            : throw new FormatException($"name '{name}' must be lowercase letters, digits and single hyphens, at most 64 characters");

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();
}
