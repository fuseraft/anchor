using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

public sealed record SessionInfo(string Id, string Workspace, DateTime Updated, string Title);

/// <summary>Append-only JSONL per session: a header, then "append" and "reset" records that replay to the current history.</summary>
public sealed class SessionLog
{
    static readonly JsonSerializerOptions Json = AIJsonUtilities.DefaultOptions;

    readonly string _path;
    readonly string _workspace;
    readonly string _model;
    List<ChatMessage> _persisted = [];

    SessionLog(string path, string id, string workspace, string model)
    {
        _path = path;
        Id = id;
        _workspace = workspace;
        _model = model;
    }

    public string Id { get; }

    public static SessionLog Create(string dir, string workspace, string model)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        return new SessionLog(Path.Combine(dir, id + ".jsonl"), id, workspace, model);
    }

    /// <summary>Opens a session by id or unique id prefix; null id means the latest session for <paramref name="workspace"/>.</summary>
    public static (SessionLog Log, List<ChatMessage> History) Open(string dir, string? id, string workspace, string model)
    {
        var candidates = id is null
            ? List(dir, workspace).Take(1).Select(s => s.Id).ToList()
            : Files(dir).Select(Path.GetFileNameWithoutExtension).Where(f => f!.StartsWith(id, StringComparison.Ordinal)).ToList()!;
        if (candidates.Count != 1)
            throw new AnchorException(candidates.Count == 0
                ? id is null ? "No previous session in this directory." : $"No session matches '{id}'."
                : $"'{id}' matches {candidates.Count} sessions; use more of the id.");

        var log = new SessionLog(Path.Combine(dir, candidates[0] + ".jsonl"), candidates[0]!, workspace, model);
        log._persisted = Replay(log._path);
        return (log, [.. log._persisted]);
    }

    public static IEnumerable<SessionInfo> List(string dir, string workspace) =>
        Files(dir)
            .Select(Info)
            .Where(s => s is not null && s.Workspace == workspace)
            .OrderByDescending(s => s!.Updated)
            .Cast<SessionInfo>();

    /// <summary>Persists whatever changed since the last sync: new messages are appended; any rewrite (compaction, /clear) writes a reset.</summary>
    public void Sync(IReadOnlyList<ChatMessage> history)
    {
        var prefixIntact = history.Count >= _persisted.Count && _persisted.Select((m, i) => ReferenceEquals(m, history[i])).All(x => x);
        if (prefixIntact && history.Count == _persisted.Count)
            return;

        var record = prefixIntact
            ? new JsonObject { ["type"] = "append", ["messages"] = Serialize(history.Skip(_persisted.Count)) }
            : new JsonObject { ["type"] = "reset", ["messages"] = Serialize(history) };

        var isNew = !File.Exists(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using (var file = new StreamWriter(OpenForAppend(_path)))
        {
            if (isNew)
                file.WriteLine(new JsonObject { ["type"] = "session", ["id"] = Id, ["workspace"] = _workspace, ["model"] = _model }.ToJsonString());
            file.WriteLine(record.ToJsonString());
        }
        if (isNew && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _persisted = [.. history];
    }

    // Another process may have the file open for a moment, such as a virus scanner or another anchor listing its sessions,
    // so a write that's refused is tried again for a little while before it fails.
    static FileStream OpenForAppend(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < WriteAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    const int WriteAttempts = 6;

    // Reads leave the file open to writers, so a session being listed or resumed never stops the one writing it.
    static StreamReader OpenForRead(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    static JsonNode Serialize(IEnumerable<ChatMessage> messages) => JsonSerializer.SerializeToNode(messages.ToList(), Json)!;

    static List<ChatMessage> Replay(string path)
    {
        var history = new List<ChatMessage>();
        using var reader = OpenForRead(path);
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            var node = JsonNode.Parse(line)!;
            var type = (string?)node["type"];
            if (type is not ("append" or "reset"))
                continue;
            if (type == "reset")
                history.Clear();
            history.AddRange(node["messages"].Deserialize<List<ChatMessage>>(Json)!.Select(Normalize));
        }
        return history;
    }

    // Deserialized tool results arrive as JsonElement; providers expect the original string.
    static ChatMessage Normalize(ChatMessage message)
    {
        foreach (var result in message.Contents.OfType<FunctionResultContent>())
            result.Result = Messages.ResultText(result);
        return message;
    }

    /// <summary>True once any session has been saved; a session's file is written with its first message.</summary>
    public static bool Any(string dir) => Files(dir).Any();

    static IEnumerable<string> Files(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jsonl") : [];

    static SessionInfo? Info(string path)
    {
        try
        {
            using var reader = OpenForRead(path);
            var header = JsonNode.Parse(reader.ReadLine() ?? "{}");
            var title = "";
            for (var line = reader.ReadLine(); line is not null && title.Length == 0; line = reader.ReadLine())
                title = JsonNode.Parse(line)?["messages"]?.Deserialize<List<ChatMessage>>(Json)?.FirstOrDefault(Messages.IsUserInput)?.Text ?? "";
            return new SessionInfo((string?)header?["id"] ?? Path.GetFileNameWithoutExtension(path), (string?)header?["workspace"] ?? "",
                File.GetLastWriteTime(path), title.ReplaceLineEndings(" "));
        }
        catch (Exception e) when (e is JsonException or IOException) // unreadable, or held open by something that shares it with no one
        {
            return null;
        }
    }
}
