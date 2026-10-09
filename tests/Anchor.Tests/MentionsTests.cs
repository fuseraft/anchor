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
        Assert.Equal([new Suggestion("@src/Anchor/Cli/Tui.cs")], items);
        Assert.Contains(new Suggestion("@src/Anchor/Cli/Tui.cs"), completion.Suggest("x @Tui").Items);
        Assert.Contains(new Suggestion("@README.md"), completion.Suggest("@").Items);
        Assert.Equal([new Suggestion("/clear", "forget the conversation")], completion.Suggest("/cl").Items);
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

    [Fact]
    public void ThePrompt_SuggestsTheWordsACommandTakes()
    {
        var completion = new PromptCompletion { Servers = () => ["docs", "files"] };

        Assert.Equal((7, "mono"), Texts(completion.Suggest("/theme mo")));
        Assert.Equal((5, "login,logout"), Texts(completion.Suggest("/mcp ")));
        Assert.Equal((11, "docs"), Texts(completion.Suggest("/mcp login d")));
        Assert.Empty(completion.Suggest("/until make test").Items);
        Assert.Empty(completion.Suggest("/copy code").Items);
    }

    static (int, string) Texts((int Start, List<Suggestion> Items) s) => (s.Start, string.Join(",", s.Items.Select(i => i.Text)));
}
