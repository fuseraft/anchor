namespace Anchor.Core;

public enum Decision { Allow, Ask, Deny }

public readonly record struct Verdict(Decision Decision, string Reason = "")
{
    public static readonly Verdict Allow = new(Decision.Allow);

    public static Verdict Ask(string reason) => new(Decision.Ask, reason);

    public static Verdict Deny(string reason) => new(Decision.Deny, reason);
}

/// <summary>Pure rules: what may be read, written, or run. <c>--yolo</c> turns every Ask into Allow; it never lifts a Deny.</summary>
/// <param name="readRoots">Extra directories that may be read without asking, such as installed skills.</param>
public sealed class Policy(Workspace workspace, bool yolo = false, IEnumerable<string>? readRoots = null)
{
    readonly List<string> _readRoots = [.. (readRoots ?? []).Where(Directory.Exists).Select(r => Workspace.RealPath(Path.GetFullPath(r)))];

    public const string InsideWrite = "write inside the workspace";

    public bool Yolo { get; } = yolo;

    /// <summary>
    /// Whether shell commands run as the bash that <see cref="ShellCommand"/> reads. On Windows they run
    /// under cmd.exe, which splits and quotes differently, so nothing is judged read-only there.
    /// </summary>
    public bool ParsesShell { get; init; } = !OperatingSystem.IsWindows();

    public Verdict Read(string literal, string real)
    {
        if (Secrets.IsSecretPath(literal) || Secrets.IsSecretPath(real))
            return Verdict.Deny("secret and credential files are always denied");
        if (!workspace.IsInside(real) && !_readRoots.Any(r => real == r || real.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            return Yolo ? Verdict.Allow : Verdict.Ask("outside the workspace");
        return Verdict.Allow;
    }

    public Verdict Write(string literal, string real)
    {
        if (Secrets.IsSecretPath(literal) || Secrets.IsSecretPath(real))
            return Verdict.Deny("secret and credential files are always denied");
        if (Yolo)
            return Verdict.Allow;
        return Verdict.Ask(workspace.IsInside(real) ? InsideWrite : "outside the workspace");
    }

    /// <summary>A tool on an MCP server: runs freely when the server marks it read-only, otherwise asks.</summary>
    public Verdict External(bool readOnly) => readOnly || Yolo ? Verdict.Allow : Verdict.Ask("external tool");

    public Verdict Run(string command)
    {
        if (ShellCommand.Danger(command) is { } danger)
            return Verdict.Deny(danger);
        if (ReachesSecretFile(command))
            return Verdict.Deny("it names a path that resolves to a secret or credential file");
        if (Yolo || ParsesShell && ShellCommand.IsReadOnly(command, p => workspace.IsInside(workspace.Resolve(p))))
            return Verdict.Allow;
        return Verdict.Ask("command");
    }

    // Catches symlinks: `cat notes.txt` where notes.txt -> .env names no secret file itself.
    bool ReachesSecretFile(string command) =>
        ShellCommand.Parse(command)
            .SelectMany(c => c.Args.Concat(c.Writes))
            .Select(a => a.StartsWith('-') && a.Contains('=') ? a[(a.IndexOf('=') + 1)..] : a)
            .Where(a => a.Length > 0 && a.IndexOfAny(['*', '?', '$', '~']) < 0)
            .Any(a => Secrets.IsSecretPath(workspace.Resolve(a)));
}
