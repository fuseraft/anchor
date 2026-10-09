using System.Diagnostics;

namespace Anchor.Core;

/// <summary>The directory the agent works in: path resolution and file enumeration.</summary>
public sealed class Workspace
{
    static readonly HashSet<string> IgnoredDirs =
        [".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "target", "__pycache__", ".venv", "venv"];

    static readonly TimeSpan SecretScanTtl = TimeSpan.FromSeconds(10);

    List<string> _secretFiles = [];
    DateTime _secretScanAt = DateTime.MinValue;

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
        if (DateTime.UtcNow - _secretScanAt > SecretScanTtl)
        {
            _secretFiles = WalkFiles(Root).Where(Secrets.IsSecretPath).Take(200).ToList();
            _secretScanAt = DateTime.UtcNow;
        }
        return _secretFiles;
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

    static IEnumerable<string> WalkFiles(string dir)
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
                    if (!IgnoredDirs.Contains(Path.GetFileName(entry)) && new DirectoryInfo(entry).LinkTarget is null)
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
