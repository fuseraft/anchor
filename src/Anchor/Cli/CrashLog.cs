using System.Runtime.InteropServices;
using Anchor.Core;

namespace Anchor.Cli;

/// <summary>
/// Records a bug in a file the user can attach to a report, so a stack trace never fills their terminal: a crash-*.log in
/// ~/.anchor/logs, readable only by them. The newest few are kept.
/// </summary>
public static class CrashLog
{
    const int Kept = 10;

    // Failures that come from normal use rather than from a bug in anchor: the model's provider or the network, timeouts,
    // and problems for the user to fix. Provider SDKs throw their own types, recognized by namespace.
    static readonly string[] ProviderNamespaces = ["Anthropic", "OpenAI", "System.ClientModel"];

    /// <summary>How anchor was started (full screen, plain, -p or --json), for the record.</summary>
    public static string Mode { get; set; } = "unknown";

    /// <summary>Set ANCHOR_DEBUG=1 to see a bug's stack trace on the terminal too.</summary>
    public static bool Debug => Environment.GetEnvironmentVariable("ANCHOR_DEBUG") == "1";

    public static string Dir => Path.Combine(AnchorHome.Dir, "logs");

    /// <summary>Whether <paramref name="e"/> is a bug, rather than a provider, network or user problem.</summary>
    public static bool IsBug(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is HttpRequestException or IOException or TimeoutException or TaskCanceledException or AnchorException
                || (x.GetType().Namespace is { } ns && ProviderNamespaces.Any(p => ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal))))
                return false;
        return true;
    }

    /// <summary>Writes <paramref name="e"/> and what anchor was running, and returns the file's path; null if it couldn't be written.</summary>
    public static string? Write(Exception e, string? dir = null)
    {
        dir ??= Dir;
        try
        {
            Directory.CreateDirectory(dir);
            // The time first, so the names sort oldest first; a few random characters, so two crashes never share a file.
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(path, options))
            {
                writer.WriteLine($"anchor {Options.Version} ({Mode})");
                writer.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}");
                writer.WriteLine($"{DateTimeOffset.Now:O}");
                writer.WriteLine();
                writer.WriteLine(e);
            }
            foreach (var old in Directory.GetFiles(dir, "crash-*.log").Order(StringComparer.Ordinal).SkipLast(Kept))
                File.Delete(old);
            return path;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records a bug and returns one line for the user: what went wrong, and where the details are.</summary>
    public static string Record(Exception e)
    {
        var path = Write(e);
        var message = e.Message.ReplaceLineEndings(" ").Trim();
        return $"something went wrong ({e.GetType().Name}: {message})."
               + (path is null ? "" : $" The details are in {Renderer.ShortPath(path)}; please include that file if you report it.")
               + (Debug ? $"\n{e}" : "");
    }
}
