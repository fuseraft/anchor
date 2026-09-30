using System.Text.Json;

namespace Anchor.Core;

/// <summary>
/// "Always" answers for shell programs and MCP tools, saved per workspace in a file the user owns, so a cloned project
/// can't grant itself anything. File writes are never saved: seeing each diff is the point of asking.
/// </summary>
public sealed class ApprovalStore(string path, string workspace)
{
    public sealed class Saved
    {
        public SortedSet<string> Programs { get; set; } = new(StringComparer.Ordinal);

        public SortedSet<string> Tools { get; set; } = new(StringComparer.Ordinal);
    }

    public string FilePath => path;

    public Saved Load() => Read().GetValueOrDefault(workspace) ?? new();

    /// <summary>Merges into what is on disk, so two sessions in the same directory don't overwrite each other.</summary>
    public void Add(IEnumerable<string>? programs = null, string? tool = null)
    {
        var all = Read();
        var saved = all.GetValueOrDefault(workspace) ?? new();
        saved.Programs.UnionWith(programs ?? []);
        if (tool is not null)
            saved.Tools.Add(tool);
        all[workspace] = saved;
        Write(all);
    }

    public void Clear()
    {
        var all = Read();
        if (all.Remove(workspace))
            Write(all);
    }

    Dictionary<string, Saved> Read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    void Write(Dictionary<string, Saved> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
