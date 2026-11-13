using Anchor.Core;
using Anchor.Tools;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

/// <summary>Files the model has read that change under it: by a shell command, the user, or another program.</summary>
public sealed class FileChangeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-changes-").FullName;
    readonly Gate _gate;
    readonly Toolbox _toolbox;

    public FileChangeTests()
    {
        var workspace = new Workspace(_root);
        _gate = new Gate(workspace, new Policy(workspace), new FakeApprover(Answer.Always), _ => { });
        _toolbox = new Toolbox([.. new FileTools(_gate).All(), .. new EditTools(_gate).All(), .. new ShellTool(_gate).All()])
        {
            Changes = _gate.TakeChanges,
        };
        File.WriteAllText(At("a.txt"), "one\ntwo\n");
        File.WriteAllText(At("b.txt"), "bee\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string At(string rel) => Path.Combine(_root, rel);

    Task<(string Text, bool Ok)> Invoke(string tool, Dictionary<string, object?> args) =>
        _toolbox.InvokeAsync(new FunctionCallContent("c", tool, args), default);

    [Fact]
    public void TakeChanges_ReportsEachChangeOnce()
    {
        _gate.MarkRead(At("a.txt"));
        _gate.MarkRead(At("b.txt"));
        File.WriteAllText(At("a.txt"), "one\nTWO\n");
        File.Delete(At("b.txt"));

        Assert.Equal(["a.txt", "b.txt (deleted)"], _gate.TakeChanges());
        Assert.Empty(_gate.TakeChanges());

        File.WriteAllText(At("a.txt"), "one\nTWO\nthree\n");
        Assert.Equal(["a.txt"], _gate.TakeChanges());
    }

    [Fact]
    public async Task TakeChanges_IgnoresUnreadFilesTouchesAndAnchorsOwnWrites()
    {
        _gate.MarkRead(At("a.txt"));
        File.SetLastWriteTimeUtc(At("a.txt"), DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(At("b.txt"), "changed\n");
        await _gate.WriteAsync(new FileEdit(At("a.txt"), "one\ntwo\n", "one\n"), default);

        Assert.Empty(_gate.TakeChanges());
    }

    [Fact]
    public void TakeChanges_ATouchedFileDoesntHideAChangedOne()
    {
        _gate.MarkRead(At("a.txt"));
        _gate.MarkRead(At("b.txt"));
        File.SetLastWriteTimeUtc(At("a.txt"), DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(At("b.txt"), "changed\n");

        Assert.Equal(["b.txt"], _gate.TakeChanges());
    }

    [Fact]
    public void TakeChanges_ChecksOnlyTheFilesReadMostRecently()
    {
        for (var i = 0; i <= Gate.DefaultTrackedFiles; i++)
        {
            File.WriteAllText(At($"f{i}.txt"), "x");
            _gate.MarkRead(At($"f{i}.txt"));
        }
        File.WriteAllText(At("f0.txt"), "changed");
        File.WriteAllText(At("f1.txt"), "changed");

        Assert.Equal(["f1.txt"], _gate.TakeChanges());
    }

    [Theory]
    [InlineData(1, new[] { "b.txt" })]
    [InlineData(0, new string[0])]
    public async Task TrackedFiles_SetsHowManyAreChecked_AndZeroTurnsTheChecksOff(int tracked, string[] expected)
    {
        var workspace = new Workspace(_root);
        var gate = new Gate(workspace, new Policy(workspace), new FakeApprover(Answer.Always), _ => { }) { TrackedFiles = tracked };
        gate.MarkRead(At("a.txt"));
        gate.MarkRead(At("b.txt"));
        File.WriteAllText(At("a.txt"), "changed\n");
        File.WriteAllText(At("b.txt"), "changed\n");

        Assert.Equal(expected, gate.TakeChanges());

        // A write to a file that changed is still refused, checked or not.
        var ex = await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), "one\ntwo\n", "mine\n"), default));
        Assert.Contains("a.txt", ex.Message);
        Assert.Equal("changed\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task AShellCommandsChangeIsNotedOnItsResult()
    {
        await Invoke("read_file", new() { ["path"] = "a.txt" });

        var (text, ok) = await Invoke("shell", new() { ["command"] = "printf 'one\\nsed\\n' > a.txt" });

        Assert.True(ok);
        Assert.Contains("changed while shell ran: a.txt", text);
    }

    [Fact]
    public async Task AChangeBetweenCallsIsNotedOnTheNextResult()
    {
        await Invoke("read_file", new() { ["path"] = "a.txt" });
        File.WriteAllText(At("a.txt"), "edited by the user\n");

        var (text, _) = await Invoke("list_dir", new() { ["path"] = "." });

        Assert.Contains("have changed since you read them: a.txt", text);
        Assert.DoesNotContain("while list_dir ran", text);
    }

    [Fact]
    public async Task AChangeBetweenTurnsJoinsTheAttachmentsNote()
    {
        await Invoke("read_file", new() { ["path"] = "a.txt" });
        File.WriteAllText(At("a.txt"), "edited by the user\n");
        var agent = new Agent(new FakeChatClient().Text("ok"), _toolbox, "system", _ => { })
        {
            Attachments = (_, _) => Task.FromResult<string?>("contents"),
        };

        await agent.RunTurnAsync("go on", default);

        var note = agent.History[1];
        Assert.Equal(MessageKind.Note, Messages.Kind(note));
        Assert.Contains("a.txt", note.Text);
        Assert.Contains("contents", note.Text);
        Assert.Equal("ok", agent.History[2].Text);
    }

    [Fact]
    public async Task ASubAgentKeepsItsOwnRecordOfWhatItRead()
    {
        _gate.MarkRead(At("a.txt"));

        var (subReplaced, subChanges) = await Task.Run(async () =>
        {
            Gate.ReadSeparately();
            var (_, replaced) = await Invoke("write_file", new() { ["path"] = "a.txt", ["content"] = "sub\n" });
            await Invoke("read_file", new() { ["path"] = "b.txt" });
            return (replaced, _gate.TakeChanges());
        });
        var (_, mainReplaced) = await Invoke("write_file", new() { ["path"] = "b.txt", ["content"] = "main\n" });

        Assert.False(subReplaced);
        Assert.Empty(subChanges);
        Assert.False(mainReplaced);
        Assert.Equal("bee\n", File.ReadAllText(At("b.txt")));
    }
}
