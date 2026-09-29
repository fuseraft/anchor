using Anchor.Providers;
using Anthropic.SDK.Messaging;

namespace Anchor.Tests;

public class AnthropicUsageTests
{
    [Fact]
    public void Normalize_CountsCachedTokensAsInput()
    {
        var start = new Usage { InputTokens = 100, OutputTokens = 1, CacheReadInputTokens = 900, CacheCreationInputTokens = 50 };
        var end = new Usage { OutputTokens = 42 };

        var usage = AnthropicChatClient.Normalize(start, end);

        Assert.Equal(1_050, usage.InputTokenCount);
        Assert.Equal(900, usage.CachedInputTokenCount);
        Assert.Equal(42, usage.OutputTokenCount);
    }
}
