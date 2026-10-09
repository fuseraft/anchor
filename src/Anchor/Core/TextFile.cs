using System.Security.Cryptography;
using System.Text;

namespace Anchor.Core;

/// <summary>A text file's content and the encoding it is stored in, so rewriting it keeps its encoding and byte-order mark.</summary>
public sealed record TextFile(string Text, Encoding Encoding)
{
    // Checked in this order, since UTF-32 LE's mark begins with UTF-16 LE's.
    static readonly Encoding[] Marked =
        [new UTF32Encoding(false, true), new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(true, true)];

    static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>The file's content, decoded as File.ReadAllText would; null when it doesn't exist.</summary>
    public static TextFile? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        var encoding = Marked.FirstOrDefault(e => bytes.AsSpan().StartsWith(e.Preamble)) ?? Utf8;
        return new(encoding.GetString(bytes.AsSpan(encoding.Preamble.Length)), encoding);
    }

    /// <summary>
    /// Writes <paramref name="text"/> in <paramref name="encoding"/> (UTF-8 without a mark by default) to a temporary file
    /// beside <paramref name="path"/> and renames it over the file, so a failed write never leaves it half written.
    /// A file being replaced keeps its permissions.
    /// </summary>
    public static void Save(string path, string text, Encoding? encoding = null)
    {
        encoding ??= Utf8;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>A fingerprint of <paramref name="text"/>, to tell whether a file still holds what was read.</summary>
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
