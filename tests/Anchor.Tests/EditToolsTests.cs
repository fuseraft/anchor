using System.Text.Json;
using Anchor.Core;
using Anchor.Tools;
using Microsoft.Extensions.AI;

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

        e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.cs", "\tint x;\n};", "int y;"));
        Assert.Contains("line 3", e.Message);
    }

    [Fact]
    public async Task EditFile_ToleratesWhitespaceAroundLines_WhenOnePlaceMatches()
    {
        File.WriteAllText(At("A.java"), "class A {\r\n\tString a = \"\";  \r\n\tString b = \"\";\r\n}\r\n");

        // Spaces for the tab, and no trailing spaces.
        var result = await _tools.EditFile("A.java", "    String a = \"\";\n    String b = \"\";\n", "\tString c = \"\";\n");

        Assert.Equal("class A {\r\n\tString c = \"\";\r\n}\r\n", File.ReadAllText(At("A.java")));
        Assert.StartsWith("Edited A.java at line 2", result);
    }

    [Fact]
    public async Task EditFile_LooseMatchInTwoPlaces_IsAmbiguous()
    {
        File.WriteAllText(At("a.txt"), "\tx = 1\ny\n\tx = 1\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.txt", "  x = 1", "x = 2"));

        Assert.Contains("lines 1, 3", e.Message);
        Assert.Equal("\tx = 1\ny\n\tx = 1\n", File.ReadAllText(At("a.txt")));
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
    public async Task WriteFile_ReplacesAFileOnlyOnceItHasBeenRead()
    {
        var workspace = new Workspace(_root);
        var gate = new Gate(workspace, new Policy(workspace), _approver, _ => { });
        var tools = new EditTools(gate);
        File.WriteAllText(At("a.txt"), "old\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => tools.WriteFile("a.txt", "new\n"));
        Assert.Contains("Read it first", e.Message);

        await new FileTools(gate).ReadFile("a.txt");
        await tools.WriteFile("a.txt", "new\n");
        Assert.Equal("new\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task EditFile_KeepsWhatTheUserChangedWhileApproving()
    {
        File.WriteAllText(At("a.txt"), "a\nb\nc\n");
        var tools = ToolsWhere(_ => File.WriteAllText(At("a.txt"), "user\na\nb\nc\n"));

        var result = await tools.EditFile("a.txt", "b", "B");

        Assert.Equal("user\na\nB\nc\n", File.ReadAllText(At("a.txt")));
        Assert.Contains("(+1 -1)", result);
    }

    [Fact]
    public async Task EditFile_RefusesWhenTheUsersChangeMakesTheMatchAmbiguous()
    {
        File.WriteAllText(At("a.txt"), "a\nb\n");
        var tools = ToolsWhere(_ => File.WriteAllText(At("a.txt"), "a\nb\nb\n"));

        var e = await Assert.ThrowsAsync<ToolException>(() => tools.EditFile("a.txt", "b\n", "B\n"));

        Assert.Contains("nothing was written", e.Message);
        Assert.Equal("a\nb\nb\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task EditFile_MakesSeveralEditsInOrder_WithOneApproval()
    {
        File.WriteAllText(At("a.txt"), "a\nb\nc\n");

        var result = await _tools.EditFile("a.txt", edits: [new("a", "A"), new("b", "B"), new("A\nB", "AB")]);

        Assert.Equal("AB\nc\n", File.ReadAllText(At("a.txt")));
        Assert.StartsWith("Made 3 edits to a.txt", result);
        Assert.Single(_approver.Requests);
    }

    [Fact]
    public async Task EditFile_MakesNoneOfSeveralEdits_WhenOneCantBeMade()
    {
        File.WriteAllText(At("a.txt"), "a\nb\nb\n");

        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.txt", edits: [new("a", "A"), new("b", "B")]));

        Assert.StartsWith("edits[1]: old_string matches 2 places (lines 2, 3)", e.Message);
        Assert.Equal("a\nb\nb\n", File.ReadAllText(At("a.txt")));
        Assert.Empty(_approver.Requests);
    }

    [Fact]
    public async Task EditFile_TakesOneFormOrTheOther()
    {
        File.WriteAllText(At("a.txt"), "a");

        await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.txt"));
        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.EditFile("a.txt", "a", "b", edits: [new("a", "c")]));

        Assert.Contains("not both", e.Message);
    }

    [Fact]
    public async Task EditFile_SeveralEditsArriveAsJson()
    {
        File.WriteAllText(At("a.txt"), "x\ny\nx\n");
        var editFile = _tools.All().Single(f => f.Name == "edit_file");
        var args = JsonDocument.Parse("""{"path":"a.txt","edits":[{"old_string":"x","new_string":"z","replace_all":true},{"old_string":"y","new_string":"w"}]}""");

        await editFile.InvokeAsync(new AIFunctionArguments(args.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value)));

        Assert.Equal("z\nw\nz\n", File.ReadAllText(At("a.txt")));
        Assert.Contains("\"old_string\"", editFile.JsonSchema.GetRawText());
    }

    [Theory]
    [InlineData("""[{"old_string":"x","new_string":"z"},null]""")]
    [InlineData("""[{"old_string":"x","new_string":null}]""")]
    public async Task EditFile_SaysWhichEditIsIncomplete(string edits)
    {
        File.WriteAllText(At("a.txt"), "x\n");
        var editFile = _tools.All().Single(f => f.Name == "edit_file");

        var e = await Assert.ThrowsAsync<ToolException>(async () => await editFile.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["path"] = "a.txt", ["edits"] = JsonDocument.Parse(edits).RootElement })));

        Assert.Contains("needs both old_string and new_string", e.Message);
        Assert.Equal("x\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public async Task EditFile_SeveralEdits_AreReappliedWhenTheUserChangesTheFileWhileApproving()
    {
        File.WriteAllText(At("a.txt"), "a\nb\n");
        var tools = ToolsWhere(_ => File.WriteAllText(At("a.txt"), "user\na\nb\n"));

        await tools.EditFile("a.txt", edits: [new("a\n", "A\n"), new("b\n", "B\n")]);

        Assert.Equal("user\nA\nB\n", File.ReadAllText(At("a.txt")));
    }

    EditTools ToolsWhere(Action<ApprovalRequest> meanwhile)
    {
        var workspace = new Workspace(_root);
        return new EditTools(new Gate(workspace, new Policy(workspace), new FakeApprover(Answer.Yes, meanwhile), _ => { }));
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
