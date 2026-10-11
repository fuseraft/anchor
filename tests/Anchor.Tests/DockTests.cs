using Anchor.Cli;

namespace Anchor.Tests;

public class DockTests
{
    [Fact]
    public void Bottom_PutsThePromptUnderTheTranscript()
    {
        // 24 rows, a 2-row draft, 3 suggestions: transcript 0-16, suggestions 17-19, rule 20, prompt 21-22, status 23.
        var frame = Tui.Arrange(Dock.Bottom, 24, 2, 3);
        Assert.Equal(new Tui.Frame(Transcript: 0, TranscriptHeight: 17, Suggest: 17, Rule: 20, Prompt: 21, Status: 23), frame);
    }

    [Fact]
    public void Top_MirrorsTheBottom()
    {
        // Status 0, prompt 1-2, rule 3, suggestions 4-6, transcript 7-23.
        var frame = Tui.Arrange(Dock.Top, 24, 2, 3);
        Assert.Equal(new Tui.Frame(Transcript: 7, TranscriptHeight: 17, Suggest: 4, Rule: 3, Prompt: 1, Status: 0), frame);
    }

    [Fact]
    public void TranscriptNeverGoesNegative_OnATinyScreen() =>
        Assert.Equal(0, Tui.Arrange(Dock.Top, 3, 10, 5).TranscriptHeight);

    [Theory]
    [InlineData("top", Dock.Top)]
    [InlineData("Bottom", Dock.Bottom)]
    public void Named_IgnoresCase(string name, Dock dock) => Assert.Equal(dock, Docks.Named(name));

    [Fact]
    public void Named_IsNullForAnUnknownPosition() => Assert.Null(Docks.Named("left"));

    [Fact]
    public void DockCommand_CompletesThePositions() =>
        Assert.Equal(["top"], new PromptCompletion().Suggest("/dock t").Items.Select(s => s.Text));
}
