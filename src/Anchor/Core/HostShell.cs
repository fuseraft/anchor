using System.Diagnostics;

namespace Anchor.Core;

/// <summary>
/// Starts a command line under the host's shell: bash, which on Windows is Git for Windows' bash, or cmd.exe on a Windows
/// machine without Git for Windows.
/// </summary>
public static class HostShell
{
    static readonly Lazy<string?> WindowsBash = new(() => FindGitBash(
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists));

    /// <summary>Whether commands run in bash, as <see cref="ShellCommand"/> reads them.</summary>
    public static bool IsBash => !OperatingSystem.IsWindows() || WindowsBash.Value is not null;

    /// <summary>The shell commands run in, as the model is told it.</summary>
    public static string Name => IsBash ? "bash" : "cmd.exe";

    /// <param name="posixShell">The shell to run <c>-c</c> with outside Windows.</param>
    public static ProcessStartInfo StartInfo(string command, string posixShell) =>
        !OperatingSystem.IsWindows() ? new ProcessStartInfo(posixShell, ["-c", command])
        : WindowsBash.Value is { } bash ? new ProcessStartInfo(bash, ["-c", command])
        : new ProcessStartInfo("cmd.exe") { Arguments = CmdArguments(command) };

    /// <summary>
    /// cmd.exe's arguments for a command line, passed verbatim. ArgumentList would escape the command's quotes as \",
    /// which cmd.exe doesn't understand, so "a b" reached the program as two words with the quotes left on. With /s, cmd.exe
    /// strips only the outer pair of quotes and runs the rest as written; /d skips AutoRun commands from the registry.
    /// </summary>
    public static string CmdArguments(string command) => $"/d /s /c \"{command}\"";

    /// <summary>
    /// Git for Windows' bin\bash.exe, found from git.exe on the PATH (in Git\cmd or Git\mingw64\bin) or under Program Files.
    /// Its bin\bash.exe sets up the PATH for the Unix tools, which usr\bin\bash.exe doesn't. Any other bash.exe on the PATH
    /// is passed over: System32's is WSL's, which sees a different filesystem.
    /// </summary>
    public static string? FindGitBash(IEnumerable<string> pathDirs, string programFiles, Func<string, bool> exists)
    {
        var candidates = pathDirs
            .Where(dir => exists(Path.Combine(dir, "git.exe")))
            .SelectMany(dir => new[] { Path.Combine(dir, "..", "bin", "bash.exe"), Path.Combine(dir, "..", "..", "bin", "bash.exe") })
            .Append(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
        return candidates.Where(exists).Select(Path.GetFullPath).FirstOrDefault();
    }
}
