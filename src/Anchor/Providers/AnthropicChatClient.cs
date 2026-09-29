using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.AI;

namespace Anchor.Providers;

/// <summary>Native Messages API client with a cache breakpoint on the conversation tail, so the whole history prefix is cached, not just system + tools.</summary>
internal sealed class AnthropicChatClient(AnthropicClient client) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
            updates.Add(update);
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Usage? start = null;
        await foreach (var response in client.Messages.StreamClaudeMessageAsync(Parameters(messages, options), cancellationToken))
        {
            var update = new ChatResponseUpdate
            {
                ResponseId = response.Id,
                ModelId = response.Model,
                RawRepresentation = response,
                Role = ChatRole.Assistant,
            };

            start ??= response.StreamStartMessage?.Usage;

            if (response.Delta is { } delta)
            {
                if (!string.IsNullOrEmpty(delta.Text))
                    update.Contents.Add(new Microsoft.Extensions.AI.TextContent(delta.Text));
                if (delta.StopReason is { } stop)
                    update.FinishReason = stop == "max_tokens" ? ChatFinishReason.Length : ChatFinishReason.Stop;
                if (response.Usage is { } usage)
                    update.Contents.Add(new UsageContent(Normalize(start ?? usage, usage)));
            }

            foreach (var call in response.ToolCalls ?? [])
            {
                var args = call.Arguments?.ToString();
                update.Contents.Add(new FunctionCallContent(call.Id, call.Name,
                    string.IsNullOrEmpty(args) ? [] : JsonSerializer.Deserialize<Dictionary<string, object?>>(args)));
            }

            yield return update;
        }
    }

    // Reported once per response, with OpenAI's meaning: input includes cached tokens.
    // Anthropic's input_tokens excludes cache reads and writes, and its start and delta events split the counts.
    internal static UsageDetails Normalize(Usage start, Usage end)
    {
        var read = start.CacheReadInputTokens;
        return new UsageDetails
        {
            InputTokenCount = start.InputTokens + read + start.CacheCreationInputTokens,
            OutputTokenCount = end.OutputTokens,
            CachedInputTokenCount = read,
        };
    }

    MessageParameters Parameters(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var parameters = ChatClientHelper.CreateMessageParameters(client.Messages, messages, options);
        parameters.PromptCaching = PromptCacheType.AutomaticToolsAndSystem;

        var tail = parameters.Messages?.LastOrDefault(m => m.Content is { Count: > 0 })?.Content?[^1];
        if (tail is { CacheControl: null })
            tail.CacheControl = new CacheControl { Type = CacheControlType.ephemeral };
        return parameters;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => client.Dispose();
}
