using Anchor.Core;

namespace Anchor.Tests;

public class ProcessOutputTests
{
    [Fact]
    public void KeepsEverything_WhileItFits()
    {
        var output = new ProcessOutput(100);
        output.Add("one");
        output.Add("two");

        Assert.Equal("one\ntwo\n", output.ToString());
    }

    [Fact]
    public void KeepsTheStartAndTheEnd_AndCountsWhatItLeavesOut()
    {
        var output = new ProcessOutput(40);
        for (var i = 0; i < 1_000; i++)
            output.Add($"line {i}");

        var text = output.ToString();
        Assert.StartsWith("line 0\nline 1\n", text);
        Assert.EndsWith("line 998\nline 999\n", text);
        Assert.Contains("characters of output left out ...]", text);
        Assert.True(text.Length < 120, $"{text.Length} characters kept");
    }

    [Fact]
    public void LeavesOutALineTooLongToKeep_AsAWhole()
    {
        var output = new ProcessOutput(100);
        output.Add(new string('x', 500));

        Assert.Equal("[a line of 500 characters, left out]\n", output.ToString());
    }

    [Fact]
    public void IsEmpty_UntilALineArrives()
    {
        var output = new ProcessOutput();
        Assert.True(output.IsEmpty);

        output.Add("");
        Assert.False(output.IsEmpty);
    }
}
