using Anchor.Cli;

namespace Anchor.Tests;

public class BellTests
{
    static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Rings_OnlyOnceTheUserHasBeenAwayAWhile()
    {
        Assert.False(Tui.ShouldRing(true, Now - TimeSpan.FromSeconds(2), Now));
        Assert.True(Tui.ShouldRing(true, Now - Tui.Away, Now));
    }

    [Fact]
    public void NeverRings_WhenTurnedOff() => Assert.False(Tui.ShouldRing(false, Now - TimeSpan.FromHours(1), Now));
}
