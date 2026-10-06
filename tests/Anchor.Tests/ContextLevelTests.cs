using Anchor.Cli;

namespace Anchor.Tests;

public class ContextLevelTests
{
    [Theory]
    [InlineData(10_000, 5_000, Repl.ContextLevel.Clear)]
    [InlineData(70_000, 5_000, Repl.ContextLevel.Clear)]
    [InlineData(70_000, 20_000, Repl.ContextLevel.Near)]
    [InlineData(80_000, 0, Repl.ContextLevel.Clear)]
    [InlineData(80_001, 0, Repl.ContextLevel.Compacting)]
    public void ContextLevelOf_FollowsTheTriggerAndTheLastTurn(long tokens, long lastGrowth, Repl.ContextLevel expected) =>
        Assert.Equal(expected, Repl.ContextLevelOf(tokens, lastGrowth, trigger: 80_000));
}
