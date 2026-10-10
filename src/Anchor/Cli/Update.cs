using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Anchor.Core;

namespace Anchor.Cli;

/// <summary>
/// Updates a copy of anchor installed from a release archive. An interactive session looks for a newer release at most
/// once a day and downloads it into ~/.anchor/update/&lt;version&gt;, checked against the release's SHA256SUMS. The next
/// start runs the download instead, and the download copies itself over the old binary. The old binary never replaces
/// itself: a single-file program still reads its own file after it starts, so it fails once that file changes.
/// A copy a package manager installed is left to that package manager.
/// </summary>
public static class Update
{
    const string Repo = "https://github.com/fuseraft/anchor";
    // Set by the old version for the new one: the path to install itself at.
    const string TargetVariable = "ANCHOR_UPDATE_TARGET";
    static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    static string Dir => Path.Combine(AnchorHome.Dir, "update");
    static string ExeName => OperatingSystem.IsWindows() ? "anchor.exe" : "anchor";

    /// <summary>
    /// Runs a downloaded release newer than this one, with the same arguments, and returns its exit code. Null means
    /// carry on as this version: nothing is waiting, or this is the new version and it has just installed itself.
    /// </summary>
    public static int? ApplyStaged(string[] args)
    {
        if (Environment.GetEnvironmentVariable(TargetVariable) is { Length: > 0 } target)
        {
            Environment.SetEnvironmentVariable(TargetVariable, null);
            try
            {
                Swap(Environment.ProcessPath!, target);
                Console.Error.WriteLine($"anchor: updated to {Parse(Options.Version)}.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"anchor: couldn't install {Parse(Options.Version)} at {target}: {e.Message}");
            }
            return null;
        }

        if (Release() is not { } exe)
            return null;
        CleanUp(exe);
        if (Staged() is not var (_, staged))
            return null;
        // Claimed first, so two starts at the same moment don't both install it.
        var claimed = Path.Combine(Dir, $"claimed-{Environment.ProcessId}");
        try
        {
            Directory.Move(Path.GetDirectoryName(staged)!, claimed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Run(Path.Combine(claimed, ExeName), args, exe);
    }

    /// <summary>Looks for a newer release in the background, unless that was done in the last day, and downloads it.</summary>
    public static void Start(Config config, Action<string> notify)
    {
        var stamp = Path.Combine(Dir, "checked");
        if (config.AutoUpdate == false || Release() is not { } exe || DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < Interval)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(stamp, "");
                if (await DownloadAsync(exe) is { } message)
                    notify(message);
            }
            catch
            {
                // Offline, rate-limited or anything else: it's tried again tomorrow.
            }
        });
    }

    static async Task<string?> DownloadAsync(string exe)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // /releases/latest redirects to the latest release's tag, without the API's rate limit.
        string? tag;
        using (var latest = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{Repo}/releases/latest")))
            tag = latest.RequestMessage?.RequestUri?.Segments[^1];
        if (tag is not ['v', .. var version] || !IsNewer(version, Options.Version) || Staged()?.Version == version)
            return null;

        if (PackageManager(File.ResolveLinkTarget(exe, returnFinalTarget: true)?.FullName ?? exe) is { } command)
            return $"anchor {version} is out: update with {command}.";
        if (!CanWrite(Path.GetDirectoryName(exe)!))
            return $"anchor {version} is out: install it with the install script (see {Repo}).";
        if (Rid() is not { } rid)
            return null;

        var zip = OperatingSystem.IsWindows();
        var archive = $"anchor-{version}-{rid}.{(zip ? "zip" : "tar.gz")}";
        var sums = await http.GetStringAsync($"{Repo}/releases/download/{tag}/SHA256SUMS");
        var bytes = await http.GetByteArrayAsync($"{Repo}/releases/download/{tag}/{archive}");
        if (!string.Equals(Expected(sums, archive), Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            return $"The download of anchor {version} didn't match its checksum, so it won't be installed.";

        var temp = Path.Combine(Dir, "download.tmp");
        bool found;
        using (var file = File.Create(temp))
            found = Extract(bytes, zip, file);
        if (!found)
        {
            File.Delete(temp);
            return null;
        }
        if (!zip)
            File.SetUnixFileMode(temp, (UnixFileMode)0b111_101_101);
        var dir = Directory.CreateDirectory(Path.Combine(Dir, version)).FullName;
        File.Move(temp, Path.Combine(dir, ExeName), overwrite: true);
        return $"anchor {version} is downloaded, and replaces this version the next time anchor starts.";
    }

    /// <summary>The running binary, if it's a release build of anchor (not a development build or the tests).</summary>
    static string? Release() =>
        Environment.ProcessPath is { } exe && Path.GetFileName(exe) == ExeName && Parse(Options.Version) is not null ? exe : null;

    /// <summary>The newest download that's newer than this version.</summary>
    static (string Version, string Path)? Staged()
    {
        if (!Directory.Exists(Dir))
            return null;
        (string Version, string Path)? newest = null;
        foreach (var dir in Directory.EnumerateDirectories(Dir))
        {
            var version = Path.GetFileName(dir);
            var exe = Path.Combine(dir, ExeName);
            if (IsNewer(version, newest?.Version ?? Options.Version) && File.Exists(exe))
                newest = (version, exe);
        }
        return newest;
    }

    // Removes what earlier updates left: downloads that aren't newer (claimed ones included) and Windows' renamed binary.
    static void CleanUp(string exe)
    {
        if (OperatingSystem.IsWindows())
            TryDelete(exe + ".old");
        if (!Directory.Exists(Dir))
            return;
        foreach (var dir in Directory.EnumerateDirectories(Dir).Where(d => !IsNewer(Path.GetFileName(d), Options.Version)))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A session still running from it; next time.
            }
        }
    }

    /// <summary>Replaces <paramref name="exe"/> with a copy of <paramref name="source"/>, keeping its permissions.</summary>
    internal static void Swap(string source, string exe)
    {
        // Copied beside it first, so the replacement is a rename on the same disk and never half a file.
        var temp = $"{exe}.{Environment.ProcessId}.new";
        File.Copy(source, temp, overwrite: true);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, File.GetUnixFileMode(exe));
            if (OperatingSystem.IsWindows())
            {
                // Windows won't replace a program that's running, but it will rename one.
                File.Move(exe, exe + ".old", overwrite: true);
                try
                {
                    File.Move(temp, exe);
                }
                catch
                {
                    File.Move(exe + ".old", exe);
                    throw;
                }
            }
            else
            {
                File.Move(temp, exe, overwrite: true);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    static int Run(string exe, string[] args, string target)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        environment[TargetVariable] = target;
        // exec only returns if it failed.
        if (!OperatingSystem.IsWindows())
            execve(exe, [exe, .. args, null], [.. environment.Select(e => $"{e.Key}={e.Value}"), null]);
        // Windows has no exec: the new version runs as a child on this console, and Ctrl+C is left to it.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment[TargetVariable] = target;
        using var child = Process.Start(start)!;
        child.WaitForExit();
        return child.ExitCode;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int execve(string path, string?[] argv, string?[] envp);

    /// <summary>Whether <paramref name="candidate"/> is a later release than <paramref name="current"/>. Pre-releases and development builds are never either.</summary>
    internal static bool IsNewer(string candidate, string current) => Parse(candidate) is { } c && Parse(current) is { } v && c > v;

    static Version? Parse(string version)
    {
        var plus = version.IndexOf('+');
        if (plus >= 0)
            version = version[..plus];
        return !version.Contains('-') && Version.TryParse(version, out var v) ? v : null;
    }

    /// <summary>The checksum SHA256SUMS lists for <paramref name="archive"/>.</summary>
    internal static string? Expected(string sums, string archive) =>
        sums.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == archive)?[0];

    /// <summary>The command that updates anchor when a package manager installed it at <paramref name="path"/>.</summary>
    internal static string? PackageManager(string path)
    {
        path = path.Replace('\\', '/');
        return path.Contains("/Cellar/", StringComparison.Ordinal) ? "brew upgrade anchor"
            : path.Contains("/apps/anchor/current/", StringComparison.OrdinalIgnoreCase) ? "scoop update anchor"
            : path.Contains("/WinGet/", StringComparison.OrdinalIgnoreCase) ? "winget upgrade Fuseraft.Anchor"
            : null;
    }

    /// <summary>Copies the anchor binary out of a release archive into <paramref name="destination"/>; false if it isn't there.</summary>
    internal static bool Extract(byte[] archive, bool zip, Stream destination)
    {
        using var input = new MemoryStream(archive);
        if (zip)
        {
            using var entries = new ZipArchive(input);
            if (entries.Entries.FirstOrDefault(e => e.Name == "anchor.exe") is not { } entry)
                return false;
            using var data = entry.Open();
            data.CopyTo(destination);
            return true;
        }
        using var tar = new TarReader(new GZipStream(input, CompressionMode.Decompress));
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == "anchor"
                && entry.DataStream is { } data)
            {
                data.CopyTo(destination);
                return true;
            }
        }
        return false;
    }

    static string? Rid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
        var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
        return os is null || arch is null ? null : $"{os}-{arch}";
    }

    static bool CanWrite(string dir)
    {
        try
        {
            using (File.Create(Path.Combine(dir, $".anchor-{Guid.NewGuid():N}"), 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
