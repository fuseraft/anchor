using Anchor.Cli;
using Anchor.Core;

namespace Anchor.Tests;

public class RendererTests
{
    static readonly Style Dim = new(0, false, true);
    static readonly Style Blue = new(94, false, false);
    static readonly Style Magenta = new(35, false, false);

    [Fact]
    public void ToolStarted_PicksOutTheToolName()
    {
        var t = new Transcript();
        new Renderer(t, color: true).Render(new ToolStarted("1", "read_file", "a.cs"));

        Assert.Equal([new Span("  ", default), new Span("↳ read_file", Blue), new Span(" a.cs", Dim)], t.Lines()[0]);
    }

    [Fact]
    public void SubAgentEvents_AreTaggedWithTheAgent()
    {
        var t = new Transcript();
        new Renderer(t, color: true).Render(new SubAgentEvent("explorer", new ToolStarted("1", "grep", "Scrolled")));

        Assert.Equal(new Span("[explorer]", Magenta), t.Lines()[0][1]);
        Assert.Equal(new Span("↳ grep", Blue), t.Lines()[0][3]);
    }

    [Fact]
    public void WithoutColor_LinesReadAsBefore()
    {
        var output = new StringWriter();
        var renderer = new Renderer(output, color: false);
        renderer.Render(new ToolStarted("1", "shell", ""));
        renderer.Render(new SubAgentEvent("explorer", new ToolStarted("2", "grep", "Scrolled")));
        renderer.Line(renderer.AllowPrompt("edits in src/"));
        renderer.Line(renderer.Answered(Answer.Always));
        renderer.Line(renderer.Prompt + "hi");

        Assert.Equal(
            "  ↳ shell\n    [explorer] ↳ grep Scrolled\nAllow? [y]es [n]o [a]lways: edits in src/\n  Allow? always\n› hi\n",
            output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Answered_IsRedForNoAndGreenOtherwise()
    {
        var renderer = new Renderer(new StringWriter(), color: true);

        Assert.Contains("\e[31mno", renderer.Answered(Answer.No));
        Assert.Contains("\e[32myes", renderer.Answered(Answer.Yes));
    }
}
