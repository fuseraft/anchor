using System.Diagnostics;

namespace Anchor.Core;

/// <summary>The directory the agent works in: path resolution and file enumeration.</summary>
public sealed class Workspace
{
    static readonly HashSet<string> IgnoredDirs =
        [".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "target", "__pycache__", ".venv", "venv"];

    static readonly TimeSpan SecretScanTtl = TimeSpan.FromSeconds(10);

    List<string> _secretFiles = [];
    HashSet<string> _outwardLinks = [];
    DateTime _scanAt = DateTime.MinValue;

    public Workspace(string root) => Root = RealPath(Path.GetFullPath(root));

    public string Root { get; }

    /// <summary>Full path with every symlink resolved, so policy sees where a path really points.</summary>
    public string Resolve(string? path) =>
        RealPath(Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path, Root));

    public string Relative(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    public bool IsInside(string full) =>
        full == Root || full.StartsWith(Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Readable files under <paramref name="dir"/> (full paths), honoring .gitignore inside a git repo.</summary>
    public IEnumerable<string> Files(string dir) =>
        (GitFiles(dir) ?? WalkFiles(dir)).Where(f => !Secrets.IsSecretPath(f) && !LinksAway(f));

    /// <summary>Secret files anywhere in the workspace, including gitignored ones.</summary>
    public IReadOnlyList<string> SecretFiles()
    {
        Scan();
        return _secretFiles;
    }

    /// <summary>
    /// Names of symlinks in the workspace that resolve outside it. Shell commands that might pass through one aren't
    /// read-only, since the bash reader can't follow every path a command reaches (after cd, through a glob).
    /// </summary>
    public IReadOnlySet<string> OutwardLinkNames()
    {
        Scan();
        return _outwardLinks;
    }

    void Scan()
    {
        if (DateTime.UtcNow - _scanAt <= SecretScanTtl)
            return;
        var secrets = new List<string>();
        var links = new HashSet<string>();
        foreach (var entry in WalkFiles(Root, dirLinks: true))
        {
            if (secrets.Count < 200 && Secrets.IsSecretPath(entry))
                secrets.Add(entry);
            if (IsLink(entry) && !IsInside(RealPath(entry)))
                links.Add(Path.GetFileName(entry));
        }
        (_secretFiles, _outwardLinks, _scanAt) = (secrets, links, DateTime.UtcNow);
    }

    static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    bool LinksAway(string file)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
            return false;
        var real = RealPath(file);
        return !IsInside(real) || Secrets.IsSecretPath(real);
    }

    public static string RealPath(string full)
    {
        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        var parts = new Queue<string>(full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        var hops = 0;
        while (parts.TryDequeue(out var part))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is { } target && hops++ < 40)
            {
                var resolved = Path.GetFullPath(target, current);
                var rest = parts.ToList();
                var resolvedRoot = Path.GetPathRoot(resolved) ?? "/";
                parts = new Queue<string>(resolved[resolvedRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Concat(rest));
                current = resolvedRoot;
                continue;
            }
            current = next;
        }
        return current;
    }

    static List<string>? GitFiles(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"])
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            // Drained, though unused: a git that warns enough to fill the pipe would otherwise wait on it, and so would we.
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                return null;
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => Path.GetFullPath(f, dir))
                .Where(File.Exists)
                .ToList();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Files under <paramref name="dir"/>, skipping ignored folders; linked folders are yielded only with <paramref name="dirLinks"/>.</summary>
    static IEnumerable<string> WalkFiles(string dir, bool dirLinks = false)
    {
        var pending = new Stack<string>([dir]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current).ToList(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (new DirectoryInfo(entry).LinkTarget is not null)
                    {
                        if (dirLinks)
                            yield return entry;
                    }
                    else if (!IgnoredDirs.Contains(Path.GetFileName(entry)))
                        pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }
}
