using Anchor.Core;

namespace Anchor.Tests;

public sealed class UndoTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-undo-").FullName;
    readonly Gate _gate;

    public UndoTests()
    {
        var workspace = new Workspace(_root);
        _gate = new Gate(workspace, new Policy(workspace, yolo: true), new FakeApprover(Answer.Yes), _ => { });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string At(string rel) => Path.Combine(_root, rel);

    Task Write(string rel, string content)
    {
        _gate.MarkRead(At(rel));
        return _gate.WriteAsync(new FileEdit(At(rel), File.Exists(At(rel)) ? File.ReadAllText(At(rel)) : null, content), default);
    }

    [Fact]
    public async Task RestoresEditsAndRemovesCreatedFiles()
    {
        File.WriteAllText(At("a.txt"), "original");
        _gate.BeginTurn();
        await Write("a.txt", "one");
        await Write("a.txt", "two");
        await Write("new.txt", "fresh");

        var (restored, skipped) = _gate.Undo();

        Assert.Equal(["a.txt", "new.txt"], restored.Order());
        Assert.Empty(skipped);
        Assert.Equal("original", File.ReadAllText(At("a.txt")));
        Assert.False(File.Exists(At("new.txt")));
    }

    [Fact]
    public async Task SkipsFilesChangedAfterAnchorWroteThem()
    {
        _gate.BeginTurn();
        await Write("a.txt", "anchor's");
        File.WriteAllText(At("a.txt"), "user's");

        var (restored, skipped) = _gate.Undo();

        Assert.Empty(restored);
        Assert.Equal(["a.txt"], skipped);
        Assert.Equal("user's", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task UndoesOneTurnAtATime_SkippingTurnsWithoutChanges()
    {
        _gate.BeginTurn();
        await Write("a.txt", "1");
        _gate.BeginTurn();
        await Write("a.txt", "2");
        _gate.BeginTurn();
        _gate.BeginTurn();

        _gate.Undo();
        Assert.Equal("1", File.ReadAllText(At("a.txt")));
        _gate.Undo();
        Assert.False(File.Exists(At("a.txt")));
        Assert.Equal((0, 0), (_gate.Undo().Restored.Count, _gate.Undo().Skipped.Count));
    }
}
