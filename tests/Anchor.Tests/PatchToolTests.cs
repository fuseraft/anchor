using Anchor.Core;
using Anchor.Tools;

namespace Anchor.Tests;

public sealed class PatchToolTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-patch-").FullName;
    readonly FakeApprover _approver = new(Answer.Yes);
    readonly Gate _gate;
    readonly PatchTool _tool;

    public PatchToolTests()
    {
        var workspace = new Workspace(_root);
        _gate = new Gate(workspace, new Policy(workspace), _approver, _ => { });
        _tool = new PatchTool(_gate);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string At(string rel) => Path.Combine(_root, rel);

    static string Patch(params string[] lines) => string.Join('\n', ["*** Begin Patch", .. lines, "*** End Patch"]);

    [Fact]
    public async Task AppliesChangesToSeveralFiles_WithOneApproval()
    {
        File.WriteAllText(At("a.txt"), "one\ntwo\n");
        File.WriteAllText(At("old.txt"), "gone\n");

        var result = await _tool.ApplyPatch(Patch(
            "*** Update File: a.txt", " one", "-two", "+2",
            "*** Add File: src/new.txt", "+fresh",
            "*** Delete File: old.txt"));

        Assert.Equal("Success. Updated the following files:\nM a.txt (+1 -1)\nA src/new.txt (+1 -0)\nD old.txt (+0 -1)", result);
        Assert.Equal("one\n2\n", File.ReadAllText(At("a.txt")));
        Assert.Equal("fresh\n", File.ReadAllText(At("src/new.txt")));
        Assert.False(File.Exists(At("old.txt")));
        Assert.Equal("Change 3 files: a.txt, src/new.txt, old.txt", Assert.Single(_approver.Requests).Title);
    }

    [Fact]
    public async Task WritesNothing_WhenAnyFileCantBeChanged()
    {
        File.WriteAllText(At("a.txt"), "one\n");
        File.WriteAllText(At("b.txt"), "two\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tool.ApplyPatch(Patch(
            "*** Update File: a.txt", "-one", "+1",
            "*** Update File: b.txt", "-three", "+3")));

        Assert.StartsWith("Update File b.txt: these lines aren't in the file", e.Message);
        Assert.Equal("one\n", File.ReadAllText(At("a.txt")));
        Assert.Empty(_approver.Requests);
    }

    [Theory]
    [InlineData("*** Add File: a.txt\n+x", "already exists")]
    [InlineData("*** Update File: missing.txt\n-x\n+y", "no such file. Create it with Add File")]
    [InlineData("*** Delete File: missing.txt", "no such file")]
    [InlineData("*** Delete File: .env", "always denied")]
    public async Task RefusesWhatItCantDo(string body, string message)
    {
        File.WriteAllText(At("a.txt"), "x\n");
        File.WriteAllText(At(".env"), "KEY=value\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tool.ApplyPatch($"*** Begin Patch\n{body}\n*** End Patch"));

        Assert.Contains(message, e.Message);
    }

    [Fact]
    public async Task MovesAFile_AndUndoMovesItBack()
    {
        File.WriteAllText(At("a.txt"), "x\ny\n");
        _gate.BeginTurn();

        var result = await _tool.ApplyPatch(Patch("*** Update File: a.txt", "*** Move to: b/a.txt", " x", "-y", "+Y"));

        Assert.Contains("M a.txt -> b/a.txt (+1 -1)", result);
        Assert.False(File.Exists(At("a.txt")));
        Assert.Equal("x\nY\n", File.ReadAllText(At("b/a.txt")));

        _gate.Undo();
        Assert.Equal("x\ny\n", File.ReadAllText(At("a.txt")));
        Assert.False(File.Exists(At("b/a.txt")));
    }

    [Fact]
    public async Task KeepsWhatTheUserChangedWhileApproving()
    {
        File.WriteAllText(At("a.txt"), "a\nb\n");
        var workspace = new Workspace(_root);
        var tool = new PatchTool(new Gate(workspace, new Policy(workspace),
            new FakeApprover(Answer.Yes, _ => File.WriteAllText(At("a.txt"), "user\na\nb\n")), _ => { }));

        await tool.ApplyPatch(Patch("*** Update File: a.txt", " a", "-b", "+B"));

        Assert.Equal("user\na\nB\n", File.ReadAllText(At("a.txt")));
    }
}
