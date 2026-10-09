using Anchor.Core;

namespace Anchor.Tests;

/// <summary>Parsing apply_patch text, and applying an update's sections to a file's content.</summary>
public class PatchTests
{
    static string Patch(params string[] lines) => string.Join('\n', ["*** Begin Patch", .. lines, "*** End Patch"]);

    static string Update(string text, params string[] lines)
    {
        var op = Assert.Single(Core.Patch.Parse(Patch(["*** Update File: a.txt", .. lines])));
        return Core.Patch.Apply(text, op.Sections!, out var problem) ?? throw new Xunit.Sdk.XunitException(problem);
    }

    [Fact]
    public void Parse_ReadsEveryKindOfFileChange()
    {
        var ops = Core.Patch.Parse(Patch(
            "*** Add File: new.txt", "+one", "+two",
            "*** Delete File: old.txt",
            "*** Update File: a.txt", "*** Move to: b.txt", "@@ class A", " x", "-y", "+z"));

        Assert.Equal([Core.Patch.Kind.Add, Core.Patch.Kind.Delete, Core.Patch.Kind.Update], ops.Select(o => o.Kind));
        Assert.Equal("one\ntwo\n", ops[0].Content);
        Assert.Equal("b.txt", ops[2].MoveTo);
        var section = Assert.Single(ops[2].Sections!);
        Assert.Equal("class A", section.Anchor);
        Assert.Equal([" x", "-y", "+z"], section.Lines);
    }

    [Fact]
    public void Parse_IgnoresAWrapperAroundThePatch()
    {
        var ops = Core.Patch.Parse("apply_patch <<'EOF'\n" + Patch("*** Delete File: a.txt") + "\nEOF\n");

        Assert.Equal("a.txt", Assert.Single(ops).Path);
    }

    [Theory]
    [InlineData("*** Update File: a.txt\n x", "starts with the line")]
    [InlineData("*** Begin Patch\n*** Add File: a.txt\none\n*** End Patch", "every line of the new file starts with '+'")]
    [InlineData("*** Begin Patch\n*** Update File: a.txt\n?x\n*** End Patch", "starts with ' ', '-' or '+'")]
    [InlineData("*** Begin Patch\n*** Edit File: a.txt\n*** End Patch", "Unexpected line")]
    [InlineData("*** Begin Patch\n*** End Patch", "doesn't change any files")]
    [InlineData("*** Begin Patch\n*** Delete File: a.txt\n*** Delete File: a.txt\n*** End Patch", "more than once")]
    [InlineData("*** Begin Patch\n*** Update File: a.txt\n*** End Patch", "no changes")]
    public void Parse_SaysWhatIsWrong(string patch, string message)
    {
        var e = Assert.Throws<ToolException>(() => Core.Patch.Parse(patch));

        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Apply_PlacesChangesByContext()
    {
        var text = "a\nb\nc\nd\ne\n";

        Assert.Equal("a\nB\nc\nd\nE\n", Update(text, " a", "-b", "+B", " c", "@@", " d", "-e", "+E"));
    }

    [Fact]
    public void Apply_AnAnchorPicksBetweenRepeatedContext()
    {
        var text = "def a():\n    return 1\ndef b():\n    return 1\n";

        Assert.Equal("def a():\n    return 1\ndef b():\n    return 2\n", Update(text, "@@ def b():", "-    return 1", "+    return 2"));
    }

    [Fact]
    public void Apply_MatchesLooselyButKeepsTheFilesOwnContextLines()
    {
        var text = "  keep   \nold\n";

        Assert.Equal("  keep   \nnew\n", Update(text, " keep", "-old", "+new"));
    }

    [Fact]
    public void Apply_EndOfFile_MatchesTheLastLines()
    {
        var text = "x\ny\nx\n";

        Assert.Equal("x\ny\nx\nz\n", Update(text, " x", "+z", "*** End of File"));
    }

    [Fact]
    public void Apply_BlankLinesInASectionAreContext()
    {
        var text = "a\n\nb\n";

        Assert.Equal("a\n\nB\n", Update(text, " a", "", "-b", "+B"));
    }

    [Fact]
    public void Apply_KeepsCrlfAndAMissingFinalNewline()
    {
        Assert.Equal("a\r\nB\r\nc", Update("a\r\nb\r\nc", " a", "-b", "+B"));
    }

    [Fact]
    public void Apply_SaysWhichLinesItCouldNotFind()
    {
        var op = Assert.Single(Core.Patch.Parse(Patch("*** Update File: a.txt", " a", "-b", "+B", "@@", " nowhere", "+x")));

        Assert.Null(Core.Patch.Apply("a\nb\n", op.Sections!, out var problem));

        Assert.StartsWith("section 2: these lines aren't in the file after the previous change:\nnowhere", problem);
    }
}
