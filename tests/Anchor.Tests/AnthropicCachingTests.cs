using Anchor.Providers;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

public class AnthropicCachingTests
{
    static bool Cached(AIContent c) => c.AdditionalProperties?.ContainsKey("anthropic:cache_control") == true;

    [Fact]
    public void Breakpoints_OnSystemAndTail_OnCopiesOnly()
    {
        var result = new FunctionResultContent("c1", "file contents");
        List<ChatMessage> history =
        [
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file")]),
            new(ChatRole.Tool, [result]),
        ];

        var sent = AnthropicChatClient.WithBreakpoints(history);

        Assert.True(Cached(sent[0].Contents[^1]));
        Assert.False(Cached(sent[1].Contents[^1]));
        Assert.False(Cached(sent[2].Contents[^1]));
        var tail = Assert.IsType<FunctionResultContent>(sent[3].Contents[^1]);
        Assert.True(Cached(tail));
        Assert.Equal("c1", tail.CallId);
        Assert.Equal("file contents", tail.Result);

        Assert.All(history.SelectMany(m => m.Contents), c => Assert.False(Cached(c)));
    }

    [Fact]
    public void RepeatedRequests_NeverAccumulateBreakpoints()
    {
        List<ChatMessage> history = [new(ChatRole.System, "s"), new(ChatRole.User, "one")];
        AnthropicChatClient.WithBreakpoints(history);
        history.Add(new(ChatRole.Assistant, "reply"));
        history.Add(new(ChatRole.User, "two"));

        var sent = AnthropicChatClient.WithBreakpoints(history);

        Assert.Equal(2, sent.SelectMany(m => m.Contents).Count(Cached));
    }
}
