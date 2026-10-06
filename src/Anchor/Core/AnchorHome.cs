namespace Anchor.Core;

/// <summary>anchor's per-user directory: ~/.anchor, or ANCHOR_HOME when it's set.</summary>
public static class AnchorHome
{
    public static string Dir =>
        Environment.GetEnvironmentVariable("ANCHOR_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".anchor");

    /// <summary>API keys <c>anchor setup</c> saves when there's no OS keychain. Every tool treats it as a secret file.</summary>
    public static string Credentials => Path.Combine(Dir, "credentials");
}
