using Anchor.Core;
using Anchor.Tools;

namespace Anchor.Tests;

public sealed class EditToolsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-edit-").FullName;
    readonly FakeApprover _approver = new(Answer.Yes);
    readonly EditTools _tools;

    public EditToolsTests()
    {
        var workspace = new Workspace(_root);
        _tools = new EditTools(new Gate(workspace, new Policy(workspace), _approver, _ => { }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string At(string rel) => Path.Combine(_root, rel);

    [Fact]
    public async Task WriteFile_CreatesWithParentDirectories()
    {
        var result = await _tools.WriteFile("src/new/a.cs", "class A {}\n");

        Assert.StartsWith("Created src/new/a.cs", result);
        Assert.Equal("class A {}\n", File.ReadAllText(At("src/new/a.cs")));
        Assert.Equal("Create src/new/a.cs", _approver.Requests[0].Title);
    }

    [Fact]
    public async Task WriteFile_RefusesElidedContent()
    {
        File.WriteAllText(At("a.cs"), "class A\n{\n    void M() { }\n}\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.WriteFile("a.cs", "class A\n{\n    // ... rest of the code unchanged\n}\n"));

        Assert.Contains("elided", e.Message);
        Assert.Empty(_approver.Requests);
    }

    [Fact]
    public async Task EditFile_ReplacesUniqueMatchAndReportsLine()
    {
        File.WriteAllText(At("a.txt"), "a\nb\nc\n");

        var result = await _tools.EditFile("a.txt", "b", "B");

        Assert.Equal("a\nB\nc\n", File.ReadAllText(At("a.txt")));
        Assert.Contains("at line 2", result);
    }

    [Fact]
    public async Task EditFile_AmbiguousMatchListsLinesAndChangesNothing()
    {
        File.WriteAllText(At("a.txt"), "x = 1\ny\nx = 1\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.txt", "x = 1", "x = 2"));

        Assert.Contains("lines 1, 3", e.Message);
        Assert.Equal("x = 1\ny\nx = 1\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task EditFile_ReplaceAll()
    {
        File.WriteAllText(At("a.txt"), "x\ny\nx\n");

        var result = await _tools.EditFile("a.txt", "x", "z", replace_all: true);

        Assert.Equal("z\ny\nz\n", File.ReadAllText(At("a.txt")));
        Assert.StartsWith("Replaced 2 occurrences", result);
    }

    [Fact]
    public async Task EditFile_NotFoundPointsAtIndentationMismatch()
    {
        File.WriteAllText(At("a.cs"), "class A\n{\n    int x;\n}\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.cs", "int z;", "int y;"));
        Assert.Contains("was not found", e.Message);

        e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.cs", "\tint x;\n}", "int y;"));
        Assert.Contains("line 3", e.Message);
    }

    [Fact]
    public async Task EditFile_PreservesCrlf()
    {
        File.WriteAllText(At("a.txt"), "one\r\ntwo\r\nthree\r\n");

        await _tools.EditFile("a.txt", "one\ntwo", "one\n2");

        Assert.Equal("one\r\n2\r\nthree\r\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task EditFile_DeclinedChangesNothing()
    {
        var workspace = new Workspace(_root);
        var tools = new EditTools(new Gate(workspace, new Policy(workspace), new FakeApprover(Answer.No), _ => { }));
        File.WriteAllText(At("a.txt"), "a");

        await Assert.ThrowsAsync<ToolException>(() => tools.EditFile("a.txt", "a", "b"));

        Assert.Equal("a", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task EditFile_DeniesSecrets()
    {
        File.WriteAllText(At(".env"), "KEY=value");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile(".env", "value", "other"));

        Assert.Contains("always denied", e.Message);
        Assert.Empty(_approver.Requests);
    }
}
