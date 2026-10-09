using Anchor.Cli;

namespace Anchor.Tests;

public class ClipboardTests
{
    [Fact]
    public void Osc52_SendsTheTextAsBase64() => Assert.Equal("\e]52;c;aMOpIQ==\a", Clipboard.Osc52("hé!"));
}
