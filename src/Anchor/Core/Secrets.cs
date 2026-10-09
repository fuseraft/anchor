using System.Collections;
using System.Text.RegularExpressions;

namespace Anchor.Core;

/// <summary>What counts as a secret file, and masking of secret values in process output.</summary>
public static partial class Secrets
{
    public const string Placeholder = "<secret-hidden>";
    const int MinValueLength = 8;

    static readonly HashSet<string> CredentialNames =
        ["id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".netrc", "_netrc", ".pgpass", ".git-credentials"];

    static readonly string[] HomeCredentialFiles =
        [".aws/credentials", ".netrc", ".pgpass", ".git-credentials", ".ssh/id_rsa", ".ssh/id_ecdsa", ".ssh/id_ed25519", ".ssh/id_dsa"];

    public static bool IsSecretName(string name) =>
        name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal) || CredentialNames.Contains(name);

    public static bool IsSecretPath(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return IsSecretName(name)
            || (name == "credentials" && Path.GetFileName(Path.GetDirectoryName(path)) is ".aws" or ".anchor")
            || string.Equals(Path.GetFullPath(path), Path.GetFullPath(AnchorHome.Credentials),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Matches a secret file name appearing anywhere in free text such as a shell command.</summary>
    [GeneratedRegex(@"(?<![\w.-])(\.env(\.[\w.-]+)?|id_(rsa|dsa|ecdsa|ed25519)|\.netrc|_netrc|\.pgpass|\.git-credentials|\.aws/credentials|\.anchor/credentials)(?![\w-])")]
    public static partial Regex MentionedFile();

    /// <summary>Replaces known secret values (secret-named env vars, contents of secret files) with <see cref="Placeholder"/>.</summary>
    public static string Mask(string text, Workspace workspace)
    {
        foreach (var value in Values(workspace).OrderByDescending(v => v.Length))
            text = text.Replace(value, Placeholder, StringComparison.Ordinal);
        return text;
    }

    static IEnumerable<string> Values(Workspace workspace)
    {
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Value is string v && v.Length >= MinValueLength && IsSecretEnvName((string)e.Key))
            {
                yield return v;
                // Long output keeps only some of its lines, so each line of a multi-line value (a PEM key) is masked on its own too.
                foreach (var line in v.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Length >= MinValueLength && l != v))
                    yield return line;
            }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = workspace.SecretFiles().Concat(HomeCredentialFiles.Select(f => Path.Combine(home, f))).Append(AnchorHome.Credentials);
        foreach (var file in files)
            foreach (var value in FileValues(file))
                yield return value;
    }

    static IEnumerable<string> FileValues(string file)
    {
        string[] lines;
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 1_000_000)
                yield break;
            lines = File.ReadAllLines(file);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("-----", StringComparison.Ordinal))
                continue;
            var eq = line.IndexOf('=');
            var value = eq > 0 ? line[(eq + 1)..].Trim().Trim('"', '\'') : line;
            foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Prepend(value))
                if (part.Length >= MinValueLength)
                    yield return part;
        }
    }

    // A credential word as a whole token of the name (not KEYBOARD_LAYOUT), unless the name says it's a pointer (AWS_ACCESS_KEY_ID, TOKEN_FILE).
    [GeneratedRegex(@"(?:^|[_.\-])(?:API_?KEY|ACCESS_?KEY|SECRET_?KEY|PRIVATE_?KEY|KEY|TOKEN|SECRET|PASSWORD|PASSWD|PASSPHRASE|CREDENTIALS?|CONNECTION_?STRING)(?:$|[_.\-])|(?:APIKEY|TOKEN|SECRET|PASSWORD|PASSWD|PASSPHRASE)$|^MYSQL_PWD$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretWord();

    [GeneratedRegex(@"(?:_|^)(?:ID|IDS|FILE|FILES|PATH|PATHS|DIR|URL|URI|HOST|PORT|NAME|SOCK|ENDPOINT|ENV|ENVVAR|VAR|VARS)$", RegexOptions.IgnoreCase)]
    private static partial Regex PointerSuffix();

    public static bool IsSecretEnvName(string name) => SecretWord().IsMatch(name) && !PointerSuffix().IsMatch(name);
}
