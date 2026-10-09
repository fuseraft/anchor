using Anchor.Cli;
using Anchor.Core;

namespace Anchor.Tests;

public class MentionsTests
{
    static readonly string[] Files = ["README.md", "src/Anchor/Cli/Tui.cs", "src/Anchor/Cli/Repl.cs", "src/Anchor/Core/Agent.cs", "src/main.py"];

    [Fact]
    public void Complete_OffersTheNextLevelDown()
    {
        Assert.Equal(["README.md", "src/"], FileIndex.Complete(Files, ""));
        Assert.Equal(["src/Anchor/", "src/main.py"], FileIndex.Complete(Files, "src/"));
        Assert.Equal(["src/Anchor/Cli/", "src/Anchor/Core/"], FileIndex.Complete(Files, "src/Anchor/C"));
    }

    [Fact]
    public void Complete_AlsoFindsAFileByTheStartOfItsName() =>
        Assert.Equal(["src/Anchor/Cli/Tui.cs"], FileIndex.Complete(Files, "tui"));

    [Fact]
    public void Complete_LeavesOutWhatIsAlreadyTyped() =>
        Assert.Empty(FileIndex.Complete(Files, "README.md"));

    [Fact]
    public async Task ThePrompt_SuggestsPathsAfterAnAt_AndCommandsAtTheStart()
    {
        var index = new FileIndex(new Workspace(Path.GetFullPath("../../../../.."))).Warm();
        for (var i = 0; i < 100 && index.Complete("").Count == 0; i++)
            await Task.Delay(50);
        var completion = new PromptCompletion { Files = index };

        var (start, items) = completion.Suggest("explain @src/Anchor/Cli/Tu");
        Assert.Equal(8, start);
        Assert.Equal(["@src/Anchor/Cli/Tui.cs"], items);
        Assert.Contains("@src/Anchor/Cli/Tui.cs", completion.Suggest("x @Tui").Items);
        Assert.Contains("@README.md", completion.Suggest("@").Items);
        Assert.Equal(["/clear"], completion.Suggest("/cl").Items);
        Assert.Empty(completion.Suggest("a@b").Items);
    }

    [Fact]
    public void Parse_FindsMentionedPathsInsideTheWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("anchor-mentions").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.py"), "");
            File.WriteAllText(Path.Combine(root, "b.md"), "");

            var paths = Mentions.Parse("see @src/a.py, (@b.md) and @src/ but not me@b.md, @missing.txt or @../x; @src/a.py again", new Workspace(root));

            Assert.Equal(["src/a.py", "b.md", "src"], paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
