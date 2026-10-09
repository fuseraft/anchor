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
    public void InTheTui_TextIsStyledAsItStreams()
    {
        var t = new Transcript();
        var renderer = new Renderer(t, color: true);
        renderer.Render(new TextDelta("Use **bo"));

        Assert.Equal("Use **bo", Text(t.Lines()[^1]));

        renderer.Render(new TextDelta("ld**\n- item"));
        renderer.Render(new ToolStarted("1", "grep", "x"));
        renderer.Render(new TextDelta("Found `it`"));
        renderer.Render(new TurnEnded(TurnEnd.Completed));

        Assert.Equal(["Use bold", "• item", "  ↳ grep x", "Found it", ""], t.Lines().Select(Text));
    }

    [Fact]
    public void InTheTui_ASubAgentsLinesGoAboveTheStreamingMessage()
    {
        var t = new Transcript();
        var renderer = new Renderer(t, color: true);
        renderer.Render(new TextDelta("| a |\n"));
        renderer.Render(new SubAgentEvent("explorer", new ToolStarted("1", "grep", "x")));
        renderer.Render(new TextDelta("| bbb |\n\nok"));
        renderer.Render(new TurnEnded(TurnEnd.Completed));

        Assert.Equal(["    [explorer] ↳ grep x", "a", "bbb", "", "ok", ""], t.Lines().Select(Text));
    }

    [Fact]
    public void OnAPlainTerminal_MarkdownIsShownAsWritten()
    {
        var output = new StringWriter();
        var renderer = new Renderer(output, color: true);
        renderer.Render(new TextDelta("Use **bold**\n- item"));
        renderer.Render(new TurnEnded(TurnEnd.Completed));

        Assert.Equal("Use **bold**\n- item\n", output.ToString().ReplaceLineEndings("\n"));
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

    static string Text(List<Span> spans) => string.Concat(spans.Select(s => s.Text));

    [Fact]
    public void InTheTui_ToolResultsAreKept_TitledWithTheirCall()
    {
        var renderer = new Renderer(new Transcript(), color: false);
        renderer.Render(new ToolStarted("1", "shell", "make"));
        renderer.Render(new ToolFinished("1", "shell", false, "line one\nline two"));
        renderer.Render(new SubAgentEvent("explorer", new ToolStarted("1", "grep", "x")));
        renderer.Render(new SubAgentEvent("explorer", new ToolFinished("1", "grep", true, "")));

        Assert.Equal([new ToolOutput("shell make (failed)", "line one\nline two", false), new ToolOutput("[explorer] grep x", "(no output)", false)],
            renderer.Outputs);
    }

    [Fact]
    public void InTheTui_OnlyTheNewestOutputsAreKept()
    {
        var renderer = new Renderer(new Transcript(), color: true);
        for (var i = 0; i < Renderer.KeptOutputs + 5; i++)
            renderer.Keep(new ToolOutput($"{i}", "", false));

        Assert.Equal(Renderer.KeptOutputs, renderer.Outputs.Count);
        Assert.Equal("5", renderer.Outputs[0].Title);
    }

    [Fact]
    public void OnAPlainTerminal_NothingIsKept()
    {
        var renderer = new Renderer(new StringWriter(), color: false);
        renderer.Render(new ToolStarted("1", "shell", "make"));
        renderer.Render(new ToolFinished("1", "shell", true, "ok"));

        Assert.Empty(renderer.Outputs);
    }

    [Fact]
    public void InTheTui_ALongDiffSaysCtrlOShowsTheRest()
    {
        var t = new Transcript();
        new Renderer(t, color: false).Diff(string.Join('\n', Enumerable.Range(0, 5).Select(i => $"+{i}")), maxLines: 3);

        Assert.Equal("    ... 2 more lines (Ctrl+O shows them all)", Text(t.Lines()[3]));
    }

    [Fact]
    public void Section_TakesItsLinesBackOutOfTheTranscript_EvenWithoutColor()
    {
        var t = new Transcript();
        var renderer = new Renderer(t, color: false);
        renderer.Line("before");

        var hide = renderer.Section(() => renderer.Diff("+added"));
        Assert.Contains(t.Lines(), l => Text(l) == "    +added");

        hide();
        Assert.DoesNotContain(t.Lines(), l => Text(l) == "    +added");
        Assert.Contains(t.Lines(), l => Text(l) == "before");
    }

    [Fact]
    public void Section_OnAPlainTerminal_WritesAndCannotTakeBack()
    {
        var output = new StringWriter();
        var renderer = new Renderer(output, color: false);

        renderer.Section(() => renderer.Line("kept"))();

        Assert.Equal("kept" + Environment.NewLine, output.ToString());
    }
}
