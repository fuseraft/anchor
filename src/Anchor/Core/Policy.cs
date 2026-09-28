namespace Anchor.Core;

public enum Decision { Allow, Ask, Deny }

public readonly record struct Verdict(Decision Decision, string Reason = "")
{
    public static readonly Verdict Allow = new(Decision.Allow);

    public static Verdict Ask(string reason) => new(Decision.Ask, reason);

    public static Verdict Deny(string reason) => new(Decision.Deny, reason);
}

/// <summary>Pure rules: what may be read, written, or run. <c>--yolo</c> turns every Ask into Allow; it never lifts a Deny.</summary>
public sealed class Policy(Workspace workspace, bool yolo = false)
{
    public const string InsideWrite = "write inside the workspace";

    public bool Yolo { get; } = yolo;

    public Verdict Read(string literal, string real)
    {
        if (Secrets.IsSecretPath(literal) || Secrets.IsSecretPath(real))
            return Verdict.Deny("secret and credential files are always denied");
        if (!workspace.IsInside(real))
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

    public Verdict Run(string command)
    {
        if (ShellCommand.Danger(command) is { } danger)
            return Verdict.Deny(danger);
        if (ReachesSecretFile(command))
            return Verdict.Deny("it names a path that resolves to a secret or credential file");
        if (Yolo || ShellCommand.IsReadOnly(command, p => workspace.IsInside(workspace.Resolve(p))))
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
