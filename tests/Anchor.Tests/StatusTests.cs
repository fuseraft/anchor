using Anchor.Cli;

namespace Anchor.Tests;

public class StatusTests
{
    [Theory]
    [InlineData(9.7, "9s")]
    [InlineData(65, "1m05s")]
    [InlineData(3720, "1h02m")]
    public void Elapsed_IsShortAndReadable(double seconds, string shown) =>
        Assert.Equal(shown, StatusView.Elapsed(TimeSpan.FromSeconds(seconds)));
}
