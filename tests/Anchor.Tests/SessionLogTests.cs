using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

public sealed class SessionLogTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anchor-sessions-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static List<ChatMessage> Conversation() =>
    [
        new(ChatRole.User, "fix the bug"),
        new(ChatRole.Assistant, [new TextContent("looking"), new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "a.cs" })]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", "class A {}")]),
        new(ChatRole.Assistant, "done"),
    ];

    [Fact]
    public void RoundTripsMessagesToolCallsAndKinds()
    {
        var log = SessionLog.Create(_dir, "/work", "m");
        List<ChatMessage> history = [Messages.Create(MessageKind.Summary, "earlier"), .. Conversation()];

        log.Sync(history);
        var (reopened, loaded) = SessionLog.Open(_dir, log.Id, "/work", "m");

        Assert.Equal(log.Id, reopened.Id);
        Assert.Equal(history.Select(m => (m.Role, m.Text)), loaded.Select(m => (m.Role, m.Text)));
        Assert.Equal(MessageKind.Summary, Messages.Kind(loaded[0]));
        var call = loaded[2].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.cs", call.Arguments!["path"]!.ToString());
        Assert.Equal("class A {}", loaded[3].Contents.OfType<FunctionResultContent>().Single().Result);
    }

    [Fact]
    public void AppendsOnlyNewMessages_AndWritesAResetAfterARewrite()
    {
        var log = SessionLog.Create(_dir, "/work", "m");
        var history = Conversation();

        log.Sync(history[..2]);
        log.Sync(history);
        log.Sync(history);
        history.RemoveRange(0, 2);
        history.Insert(0, Messages.Create(MessageKind.Summary, "compacted"));
        log.Sync(history);

        var lines = File.ReadAllLines(Path.Combine(_dir, log.Id + ".jsonl"));
        Assert.Equal(["session", "append", "append", "reset"], lines.Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!["type"]!.ToString()));
        Assert.Equal(["compacted", "", "done"], SessionLog.Open(_dir, log.Id, "/work", "m").History.Select(m => m.Text));
    }

    [Fact]
    public void ClearedHistory_ResumesEmpty()
    {
        var log = SessionLog.Create(_dir, "/work", "m");
        var history = Conversation();
        log.Sync(history);
        history.Clear();
        log.Sync(history);

        Assert.Empty(SessionLog.Open(_dir, log.Id, "/work", "m").History);
    }

    [Fact]
    public void NoFileUntilThereIsSomethingToSave()
    {
        SessionLog.Create(_dir, "/work", "m").Sync([]);

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void FileIsPrivateToTheUser()
    {
        if (OperatingSystem.IsWindows())
            return;
        var log = SessionLog.Create(_dir, "/work", "m");
        log.Sync(Conversation());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, log.Id + ".jsonl")));
    }

    [Fact]
    public void ListAndResumeLatest_AreScopedToTheWorkspace()
    {
        var a = Save("/work", "first task", DateTime.Now.AddMinutes(-10));
        var b = Save("/work", "second task", DateTime.Now.AddMinutes(-5));
        Save("/other", "elsewhere", DateTime.Now);

        Assert.Equal([b, a], SessionLog.List(_dir, "/work").Select(s => s.Id));
        Assert.Equal("second task", SessionLog.List(_dir, "/work").First().Title);
        Assert.Equal(b, SessionLog.Open(_dir, null, "/work", "m").Log.Id);
    }

    [Fact]
    public void Open_ByPrefix_RequiresAUniqueMatch()
    {
        var id = Save("/work", "x", DateTime.Now);

        Assert.Equal(id, SessionLog.Open(_dir, id[..15], "/work", "m").Log.Id);
        Assert.Contains("No session matches", Assert.Throws<AnchorException>(() => SessionLog.Open(_dir, "nope", "/work", "m")).Message);
        Assert.Contains("No previous session", Assert.Throws<AnchorException>(() => SessionLog.Open(_dir, null, "/empty", "m")).Message);
    }

    string Save(string workspace, string request, DateTime when)
    {
        var log = SessionLog.Create(_dir, workspace, "m");
        log.Sync([new ChatMessage(ChatRole.User, request)]);
        File.SetLastWriteTime(Path.Combine(_dir, log.Id + ".jsonl"), when);
        return log.Id;
    }
}
