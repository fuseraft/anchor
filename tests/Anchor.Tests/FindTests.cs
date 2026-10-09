using Anchor.Cli;

namespace Anchor.Tests;

public class FindTests
{
    static readonly Style Dim = new(0, false, true);

    [Fact]
    public void Matches_AreTheLinesHoldingTheText_IgnoringCase()
    {
        List<List<Span>> lines = [[new("Build ", default), new("failed", Dim)], [new("ok", default)], [new("it FAILED again", default)]];

        Assert.Equal([0, 2], Tui.Matches(lines, "failed"));
        Assert.Equal([0], Tui.Matches(lines, "d f"));
    }

    [Fact]
    public void Marked_SplitsSpansAtEachMatch_AcrossStyles()
    {
        List<Span> row = [new("a cat", default), new("Cat dog", Dim)];

        Assert.Equal([("a ", default, false), ("cat", default, true), ("Cat", Dim, true), (" dog", Dim, false)],
            TranscriptView.Marked(row, "cat"));
        Assert.Equal([("a cat", default, false), ("Cat dog", Dim, false)], TranscriptView.Marked(row, null));
    }
}
