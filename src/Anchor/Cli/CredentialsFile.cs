namespace Anchor.Cli;

/// <summary>API keys as <c>NAME=value</c> lines in a file only the user can read; <c>anchor setup</c> uses it when there's no OS keychain.</summary>
/// <remarks>The name is the variable the key stands in for, and that variable wins when it's set.</remarks>
public sealed class CredentialsFile(string path)
{
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string Path => path;

    public string? Get(string env)
    {
        if (!File.Exists(path))
            return null;
        foreach (var line in File.ReadLines(path))
            if (line.StartsWith(env + "=", StringComparison.Ordinal) && line[(env.Length + 1)..].Trim() is { Length: > 0 } value)
                return value;
        return null;
    }

    /// <summary>Adds or replaces the key, writing a new file so the old one is never left half written.</summary>
    public void Set(string env, string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException("A key can't contain a line break.", nameof(value));
        var lines = File.Exists(path) ? File.ReadLines(path).Where(l => !l.StartsWith(env + "=", StringComparison.Ordinal)).ToList() : [];
        lines.Add($"{env}={value}");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // The mode only applies to a file being created, so a leftover temp file mustn't be reused.
        var temp = path + ".tmp";
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        using (var writer = new StreamWriter(temp, options))
            writer.Write(string.Join("\n", lines) + "\n");
        File.Move(temp, path, overwrite: true);
    }
}
