using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Anchor.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Anchor.Tools;

/// <summary>Read-only workspace tools: read_file, list_dir, glob, grep.</summary>
public sealed class FileTools(Gate gate)
{
    const int MaxLineChars = 2_000;
    const int MaxEntries = 500;
    const long MaxGrepFileBytes = 2_000_000;

    Workspace Workspace => gate.Workspace;

    public IEnumerable<AIFunction> All() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListDir, "list_dir"),
        AIFunctionFactory.Create(Glob, "glob"),
        AIFunctionFactory.Create(Grep, "grep"),
    ];

    [Description("Read a text file. Returns numbered lines. Use offset and limit to read part of a large file.")]
    public async Task<string> ReadFile(
        [Description("File path, relative to the workspace root.")] string path,
        [Description("First line to read (1-based).")] int offset = 1,
        [Description("Maximum number of lines to read.")] int limit = 2000,
        CancellationToken ct = default)
    {
        var full = await gate.ReadPathAsync(path, ct);
        if (!File.Exists(full))
            throw new ToolException(Directory.Exists(full) ? $"'{path}' is a directory; use list_dir." : $"'{path}' does not exist.");
        if (IsBinary(full))
            throw new ToolException($"'{path}' is a binary file.");

        offset = Math.Max(1, offset);
        limit = Math.Max(1, limit);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var line in File.ReadLines(full))
        {
            n++;
            if (n < offset)
                continue;
            if (n >= offset + limit)
            {
                var total = n + File.ReadLines(full).Skip(n).Count();
                sb.Append($"[{total - n + 1} more lines; continue with offset={n}]\n");
                break;
            }
            sb.Append(n.ToString().PadLeft(6)).Append('\t')
              .Append(line.Length > MaxLineChars ? line[..MaxLineChars] + " [line truncated]" : line).Append('\n');
        }

        gate.MarkRead(full);
        if (sb.Length == 0)
            return n == 0 ? "(empty file)" : $"(file has {n} lines; offset {offset} is past the end)";
        return sb.ToString();
    }

    [Description("List files and directories as a tree, honoring .gitignore.")]
    public async Task<string> ListDir(
        [Description("Directory path, relative to the workspace root.")] string path = ".",
        [Description("How many levels deep to list.")] int depth = 2,
        CancellationToken ct = default)
    {
        var full = await gate.ReadPathAsync(path, ct);
        if (!Directory.Exists(full))
            throw new ToolException($"'{path}' is not a directory.");

        var entries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Workspace.Files(full))
        {
            var parts = Path.GetRelativePath(full, file).Replace('\\', '/').Split('/');
            for (var i = 0; i < Math.Min(parts.Length, Math.Max(1, depth)); i++)
                entries.Add(string.Join('/', parts[..(i + 1)]) + (i < parts.Length - 1 ? "/" : ""));
        }

        if (entries.Count == 0)
            return "(empty)";
        var shown = entries.Take(MaxEntries).ToList();
        var text = string.Join('\n', shown);
        return entries.Count > MaxEntries ? $"{text}\n[{entries.Count - MaxEntries} more entries; list a subdirectory]" : text;
    }

    [Description("Find files by glob pattern (e.g. **/*.cs), honoring .gitignore. Returns paths relative to the workspace root.")]
    public async Task<string> Glob(
        [Description("Glob pattern.")] string pattern,
        [Description("Directory to search, relative to the workspace root.")] string path = ".",
        CancellationToken ct = default)
    {
        var full = await gate.ReadPathAsync(path, ct);
        var matcher = new Matcher(StringComparison.Ordinal).AddInclude(pattern);
        var matches = Workspace.Files(full)
            .Where(f => matcher.Match(full, f).HasMatches)
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();
        return Listing(matches, "no files match");
    }

    [Description("Search file contents with a regular expression, honoring .gitignore. Returns path:line: text.")]
    public async Task<string> Grep(
        [Description("Regular expression (.NET syntax).")] string pattern,
        [Description("File or directory to search, relative to the workspace root.")] string path = ".",
        [Description("Only search files matching this glob (e.g. *.cs).")] string? glob = null,
        [Description("Match case-insensitively.")] bool ignore_case = false,
        CancellationToken ct = default)
    {
        var full = await gate.ReadPathAsync(path, ct);
        Regex regex;
        try
        {
            regex = new Regex(pattern, (ignore_case ? RegexOptions.IgnoreCase : 0) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw new ToolException($"Invalid regex: {e.Message}");
        }

        var files = File.Exists(full) ? [full] : Workspace.Files(full);
        if (glob is not null)
        {
            var matcher = new Matcher(StringComparison.Ordinal).AddInclude(glob.Contains('/') ? glob : "**/" + glob);
            files = files.Where(f => matcher.Match(full, f).HasMatches);
        }

        var hits = new List<string>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            if (new FileInfo(file).Length > MaxGrepFileBytes || IsBinary(file))
                continue;
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (!regex.IsMatch(line))
                    continue;
                var text = line.Trim();
                hits.Add($"{Relative(file)}:{n}: {(text.Length > 300 ? text[..300] + "..." : text)}");
                if (hits.Count > MaxEntries)
                    return Listing(hits, "");
            }
        }
        return Listing(hits, "no matches");
    }

    // Outside the workspace (after approval) paths are shown absolute.
    string Relative(string full) => Workspace.IsInside(full) ? Workspace.Relative(full) : full;

    static string Listing(List<string> items, string empty) =>
        items.Count == 0 ? $"({empty})"
        : items.Count > MaxEntries ? string.Join('\n', items.Take(MaxEntries)) + "\n[more results; narrow the search]"
        : string.Join('\n', items);

    static bool IsBinary(string file)
    {
        using var stream = File.OpenRead(file);
        Span<byte> buffer = stackalloc byte[8192];
        var read = stream.Read(buffer);
        return buffer[..read].Contains((byte)0);
    }
}
