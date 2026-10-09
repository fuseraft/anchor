using System.Text;
using Anchor.Core;

namespace Anchor.Tests;

/// <summary>Gate.WriteAsync: what the model must have read, files that change during approval, and how files are written.</summary>
public sealed class WriteTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-write-").FullName;
    readonly List<AgentEvent> _events = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string At(string rel) => Path.Combine(_root, rel);

    Gate NewGate(FakeApprover? approver = null)
    {
        var workspace = new Workspace(_root);
        return new Gate(workspace, new Policy(workspace), approver ?? new FakeApprover(Answer.Yes), _events.Add);
    }

    [Fact]
    public async Task ReplacingAFileTheModelHasNotRead_IsRefusedBeforeAsking()
    {
        File.WriteAllText(At("a.txt"), "theirs");
        var approver = new FakeApprover(Answer.Yes);

        var e = await Assert.ThrowsAsync<ToolException>(() => NewGate(approver).WriteAsync(new FileEdit(At("a.txt"), "theirs", "mine"), default));

        Assert.Contains("haven't read it", e.Message);
        Assert.Empty(approver.Requests);
        Assert.Equal("theirs", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task ReplacingAFileChangedSinceItWasRead_IsRefused()
    {
        File.WriteAllText(At("a.txt"), "one");
        var gate = NewGate();
        gate.MarkRead(At("a.txt"));
        File.WriteAllText(At("a.txt"), "two");

        var e = await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), "two", "three"), default));

        Assert.Contains("changed since you last read it", e.Message);
        Assert.Equal("two", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task ForgetReads_RequiresReadingAgain()
    {
        File.WriteAllText(At("a.txt"), "one");
        var gate = NewGate();
        gate.MarkRead(At("a.txt"));
        gate.ForgetReads();

        await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), "one", "two"), default));
    }

    [Fact]
    public async Task AFileWrittenWhole_CountsAsRead()
    {
        var gate = NewGate();
        await gate.WriteAsync(new FileEdit(At("a.txt"), null, "one"), default);

        await gate.WriteAsync(new FileEdit(At("a.txt"), "one", "two"), default);

        Assert.Equal("two", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task EditingPartOfAFileReadInAnOlderVersion_DoesNotCountAsReadingIt()
    {
        File.WriteAllText(At("a.txt"), "a\nb\n");
        var gate = NewGate();
        gate.MarkRead(At("a.txt"));
        File.WriteAllText(At("a.txt"), "a\nb\nformatted\n");

        await gate.WriteAsync(new FileEdit(At("a.txt"), "a\nb\nformatted\n", "a\nB\nformatted\n", _ => null), default);

        await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), "a\nB\nformatted\n", "x"), default));
    }

    [Fact]
    public async Task AFileChangedDuringApproval_IsNotOverwritten()
    {
        File.WriteAllText(At("a.txt"), "one");
        var gate = NewGate(new FakeApprover(Answer.Yes, _ => File.WriteAllText(At("a.txt"), "the user's")));
        gate.MarkRead(At("a.txt"));

        var e = await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), "one", "two"), default));

        Assert.Contains("changed after you read it", e.Message);
        Assert.Equal("the user's", File.ReadAllText(At("a.txt")));
        Assert.Equal(0, gate.Writes);
        Assert.DoesNotContain(_events, ev => ev is FileChanged);
    }

    [Fact]
    public async Task AFileCreatedDuringApproval_IsNotOverwritten()
    {
        var gate = NewGate(new FakeApprover(Answer.Yes, _ => File.WriteAllText(At("a.txt"), "the user's")));

        var e = await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), null, "mine"), default));

        Assert.Contains("was created", e.Message);
        Assert.Equal("the user's", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task AFileChangedDuringApproval_GetsTheChangeReapplied_AndMustBeReadAgain()
    {
        File.WriteAllText(At("a.txt"), "a\nb\n");
        var gate = NewGate(new FakeApprover(Answer.Yes, _ => File.WriteAllText(At("a.txt"), "a\nb\nuser\n")));
        gate.MarkRead(At("a.txt"));

        var diff = await gate.WriteAsync(new FileEdit(At("a.txt"), "a\nb\n", "a\nB\n", current => current.Replace("b\n", "B\n")), default);

        Assert.Equal("a\nB\nuser\n", File.ReadAllText(At("a.txt")));
        Assert.Equal((1, 1), (diff.Added, diff.Removed));
        // The model never saw the user's line, so replacing the whole file needs a fresh read.
        await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), "a\nB\nuser\n", "x"), default));
    }

    [Fact]
    public async Task AChangeThatNoLongerFits_IsRefused()
    {
        File.WriteAllText(At("a.txt"), "a\nb\n");
        var gate = NewGate(new FakeApprover(Answer.Yes, _ => File.WriteAllText(At("a.txt"), "a\n")));

        await Assert.ThrowsAsync<ToolException>(() =>
            gate.WriteAsync(new FileEdit(At("a.txt"), "a\nb\n", "a\nB\n", current => current.Contains("b\n") ? current.Replace("b\n", "B\n") : null), default));

        Assert.Equal("a\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task SeveralFiles_AreApprovedTogether()
    {
        File.WriteAllText(At("a.txt"), "a");
        var approver = new FakeApprover(Answer.Yes);
        var gate = NewGate(approver);
        gate.MarkRead(At("a.txt"));

        await gate.WriteAsync([new FileEdit(At("a.txt"), "a", "A"), new FileEdit(At("b.txt"), null, "b")], default);

        var request = Assert.Single(approver.Requests);
        Assert.Equal("Change 2 files: a.txt, b.txt", request.Title);
        Assert.Contains("Edit a.txt\n", request.Detail);
        Assert.Contains("Create b.txt\n", request.Detail);
        Assert.Equal(("A", "b"), (File.ReadAllText(At("a.txt")), File.ReadAllText(At("b.txt"))));
        Assert.Equal(2, _events.OfType<FileChanged>().Count());
    }

    [Fact]
    public async Task SeveralFiles_AreWrittenAllOrNone()
    {
        File.WriteAllText(At("a.txt"), "a");
        File.WriteAllText(At("b.txt"), "b");
        var gate = NewGate(new FakeApprover(Answer.Yes, _ => File.WriteAllText(At("b.txt"), "the user's")));
        gate.MarkRead(At("a.txt"));
        gate.MarkRead(At("b.txt"));

        await Assert.ThrowsAsync<ToolException>(() =>
            gate.WriteAsync([new FileEdit(At("a.txt"), "a", "A"), new FileEdit(At("b.txt"), "b", "B")], default));

        Assert.Equal(("a", "the user's"), (File.ReadAllText(At("a.txt")), File.ReadAllText(At("b.txt"))));
    }

    [Fact]
    public async Task AFailedWrite_PutsBackTheFilesAlreadyWritten()
    {
        File.WriteAllText(At("a.txt"), "a");
        File.WriteAllText(At("blocker"), "a file where a directory has to go");
        var gate = NewGate();
        gate.MarkRead(At("a.txt"));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            gate.WriteAsync([new FileEdit(At("a.txt"), "a", "A"), new FileEdit(At("blocker/b.txt"), null, "b")], default));

        Assert.Equal("a", File.ReadAllText(At("a.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Delete_RemovesTheFile_AndUndoBringsItBack()
    {
        File.WriteAllText(At("a.txt"), "content");
        var gate = NewGate();
        gate.BeginTurn();

        var diff = await gate.WriteAsync(new FileEdit(At("a.txt"), "content", null), default);

        Assert.False(File.Exists(At("a.txt")));
        Assert.Equal(1, diff.Removed);
        Assert.Equal(["a.txt"], gate.Undo().Restored);
        Assert.Equal("content", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task Edits_KeepTheFilesEncodingAndByteOrderMark()
    {
        var utf8Bom = new UTF8Encoding(true);
        File.WriteAllText(At("bom.cs"), "class A {}\n", utf8Bom);
        File.WriteAllText(At("wide.txt"), "wide\n", Encoding.Unicode);
        var gate = NewGate();
        gate.MarkRead(At("bom.cs"));
        gate.MarkRead(At("wide.txt"));

        await gate.WriteAsync([new FileEdit(At("bom.cs"), "class A {}\n", "class B {}\n"), new FileEdit(At("wide.txt"), "wide\n", "still wide\n")], default);

        Assert.Equal([.. utf8Bom.GetPreamble(), .. Encoding.UTF8.GetBytes("class B {}\n")], File.ReadAllBytes(At("bom.cs")));
        Assert.Equal([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("still wide\n")], File.ReadAllBytes(At("wide.txt")));
    }

    [Fact]
    public async Task NewFiles_AreUtf8WithoutAByteOrderMark()
    {
        await NewGate().WriteAsync(new FileEdit(At("a.txt"), null, "é"), default);

        Assert.Equal(Encoding.UTF8.GetBytes("é"), File.ReadAllBytes(At("a.txt")));
    }

    [Fact]
    public async Task Edits_KeepTheFilesPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;
        File.WriteAllText(At("run.sh"), "echo 1\n");
        File.SetUnixFileMode(At("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var gate = NewGate();
        gate.MarkRead(At("run.sh"));

        await gate.WriteAsync(new FileEdit(At("run.sh"), "echo 1\n", "echo 2\n"), default);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(At("run.sh")));
    }

    [Fact]
    public async Task TheSameFileTwice_IsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewGate().WriteAsync([new FileEdit(At("a.txt"), null, "1"), new FileEdit(At("a.txt"), null, "2")], default));
    }
}
