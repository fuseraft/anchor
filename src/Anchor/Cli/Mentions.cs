using System.Text;
using System.Text.RegularExpressions;
using Anchor.Core;
using Anchor.Tools;

namespace Anchor.Cli;

/// <summary>
/// Files and directories the user names with @ in a message, such as "@src/app.py". Each one inside the workspace is
/// read through the file tools, so the gate's rules apply as if the model had read it, and handed to the model as a
/// note next to the message.
/// </summary>
public static partial class Mentions
{
    public const int MaxFiles = 10;

    /// <summary>The workspace paths <paramref name="text"/> mentions, relative to the root, in order and without repeats.</summary>
    public static List<string> Parse(string text, Workspace workspace)
    {
        List<string> paths = [];
        foreach (Match m in Mention().Matches(text))
        {
            // "see @a.py." means a.py; the punctuation after a mention is only stripped when the path needs it.
            var token = m.Groups[1].Value;
            while (token.Length > 0 && !Exists(token) && ".,;:!?)]}'\"".Contains(token[^1]))
                token = token[..^1];
            if (token.Length == 0 || !Exists(token))
                continue;
            var relative = workspace.Relative(Path.GetFullPath(token, workspace.Root)).TrimEnd('/');
            if (relative.Length > 0 && relative != "." && !paths.Contains(relative) && paths.Count < MaxFiles)
                paths.Add(relative);
        }
        return paths;

        bool Exists(string token)
        {
            var full = Path.GetFullPath(token, workspace.Root);
            return workspace.IsInside(full) && full != workspace.Root && (File.Exists(full) || Directory.Exists(full));
        }
    }

    /// <summary>
    /// What the mentioned paths hold, as the model's note: a file as read_file shows it, a directory as list_dir does.
    /// <paramref name="report"/> hears what was attached or why not. Null when nothing was.
    /// </summary>
    public static async Task<string?> AttachAsync(string text, Gate gate, Action<string, bool> report, CancellationToken ct)
    {
        var tools = new FileTools(gate);
        var sb = new StringBuilder();
        foreach (var path in Parse(text, gate.Workspace))
        {
            string content;
            try
            {
                content = Directory.Exists(Path.GetFullPath(path, gate.Workspace.Root))
                    ? await tools.ListDir(path, ct: ct)
                    : await tools.ReadFile(path, ct: ct);
            }
            catch (ToolException e)
            {
                report($"@{path} not attached: {e.Message}", false);
                continue;
            }
            if (content.Length > Toolbox.MaxResultChars)
                content = content[..Toolbox.MaxResultChars] + $"\n[cut at {Toolbox.MaxResultChars:N0} characters; read the rest with read_file]\n";
            sb.Append($"<file path=\"{path}\">\n{content.TrimEnd('\n')}\n</file>\n");
            report($"@{path}", true);
        }
        return sb.Length == 0 ? null : "[anchor] The user's message mentions these paths with @; here they are as read_file and list_dir show them.\n\n" + sb;
    }

    [GeneratedRegex(@"(?<![^\s(\[])@([^\s@]+)")]
    private static partial Regex Mention();
}

/// <summary>
/// The workspace's files for completing @ mentions, kept in memory so a keystroke never waits for the disk. It's
/// refreshed in the background once it's older than a few seconds.
/// </summary>
public sealed class FileIndex(Workspace workspace)
{
    public const int MaxItems = 20;
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);

    volatile List<string> _files = [];
    DateTime _loaded = DateTime.MinValue;
    int _loading;

    /// <summary>Starts loading, so the first @ already has something to offer.</summary>
    public FileIndex Warm()
    {
        Refresh();
        return this;
    }

    /// <summary>
    /// Paths that complete <paramref name="typed"/>: first the entries one level down from what's typed (directories end
    /// in "/"), then files anywhere whose name starts with its last part.
    /// </summary>
    public List<string> Complete(string typed)
    {
        if (DateTime.UtcNow - _loaded > Fresh)
            Refresh();
        return Complete(_files, typed);
    }

    public static List<string> Complete(IReadOnlyList<string> files, string typed)
    {
        var slash = typed.LastIndexOf('/');
        var (dir, name) = (typed[..(slash + 1)], typed[(slash + 1)..]);
        var level = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.StartsWith(dir, StringComparison.Ordinal))
                continue;
            var rest = file[dir.Length..];
            if (!rest.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var next = rest.IndexOf('/');
            level.Add(dir + (next < 0 ? rest : rest[..(next + 1)]));
        }
        List<string> items = [.. level.Take(MaxItems)];
        if (name.Length > 0)
            foreach (var file in files)
            {
                if (items.Count >= MaxItems)
                    break;
                if (Path.GetFileName(file).StartsWith(name, StringComparison.OrdinalIgnoreCase) && !items.Contains(file))
                    items.Add(file);
            }
        return items.Where(item => item != typed).ToList();
    }

    void Refresh()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                _files = [.. workspace.Files(workspace.Root).Select(workspace.Relative).Order(StringComparer.Ordinal)];
                _loaded = DateTime.UtcNow;
            }
            catch (IOException)
            {
            }
            finally
            {
                _loading = 0;
            }
        });
    }
}
