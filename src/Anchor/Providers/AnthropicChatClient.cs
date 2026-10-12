using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Anchor.Providers;

/// <summary>The official SDK's client with cache breakpoints on the system prompt and the conversation tail,
/// so the whole history prefix is cached, not just system + tools.</summary>
/// <remarks>Breakpoints go on copies, never on the caller's messages; otherwise every past tail would keep one and
/// a long session would pass the API's limit of four.</remarks>
internal sealed class AnthropicChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(WithBreakpoints(WithoutUnsignedReasoning(messages)), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(WithBreakpoints(WithoutUnsignedReasoning(messages)), options, cancellationToken);

    /// <summary>Drops reasoning with no signature, which another provider wrote before a /model switch; the API rejects a
    /// thinking block it didn't sign. Claude's own thinking carries its signature and goes back as it came.</summary>
    internal static IEnumerable<ChatMessage> WithoutUnsignedReasoning(IEnumerable<ChatMessage> messages)
    {
        static bool Unsigned(AIContent c) => c is TextReasoningContent { ProtectedData: null or "" };

        foreach (var message in messages)
        {
            if (!message.Contents.Any(Unsigned))
            {
                yield return message;
                continue;
            }
            var clone = message.Clone();
            clone.Contents = [.. message.Contents.Where(c => !Unsigned(c))];
            if (clone.Contents.Count > 0)
                yield return clone;
        }
    }

    internal static List<ChatMessage> WithBreakpoints(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> list = [.. messages];
        var system = list.FindLastIndex(m => m.Role == ChatRole.System);
        if (system >= 0)
            list[system] = WithTailBreakpoint(list[system]);
        var tail = list.FindLastIndex(m => m.Role != ChatRole.System && m.Contents.Count > 0);
        if (tail >= 0)
            list[tail] = WithTailBreakpoint(list[tail]);
        return list;
    }

    static ChatMessage WithTailBreakpoint(ChatMessage message)
    {
        AIContent? copy = message.Contents[^1] switch
        {
            TextContent t => new TextContent(t.Text),
            FunctionResultContent r => new FunctionResultContent(r.CallId, r.Result),
            FunctionCallContent c => new FunctionCallContent(c.CallId, c.Name, c.Arguments),
            _ => null,
        };
        if (copy is null)
            return message;
        copy.WithCacheControl(new CacheControlEphemeral());
        var clone = message.Clone();
        clone.Contents = [.. message.Contents.SkipLast(1), copy];
        return clone;
    }
}
