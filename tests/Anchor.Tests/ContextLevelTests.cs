using Anchor.Cli;

namespace Anchor.Tests;

public class ContextLevelTests
{
    [Theory]
    [InlineData(10_000, 5_000, StatusLine.ContextLevel.Clear)]
    [InlineData(70_000, 5_000, StatusLine.ContextLevel.Clear)]
    [InlineData(70_000, 20_000, StatusLine.ContextLevel.Near)]
    [InlineData(80_000, 0, StatusLine.ContextLevel.Clear)]
    [InlineData(80_001, 0, StatusLine.ContextLevel.Compacting)]
    public void ContextLevelOf_FollowsTheTriggerAndTheLastTurn(long tokens, long lastGrowth, StatusLine.ContextLevel expected) =>
        Assert.Equal(expected, StatusLine.ContextLevelOf(tokens, lastGrowth, trigger: 80_000));
}
