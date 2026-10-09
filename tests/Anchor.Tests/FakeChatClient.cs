using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

/// <summary>Plays back scripted streaming responses and records every request.</summary>
sealed class FakeChatClient : IChatClient
{
    readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> _script = new();

    public List<List<ChatMessage>> Requests { get; } = [];

    /// <summary>The names of the tools each request offered.</summary>
    public List<List<string>> Tools { get; } = [];

    public FakeChatClient Text(string text, long input = 10, long output = 5) =>
        Enqueue(Updates(new TextContent(text), new UsageContent(new() { InputTokenCount = input, OutputTokenCount = output })));

    public FakeChatClient Calls(params FunctionCallContent[] calls) => Enqueue(Updates(calls));

    public FakeChatClient Call(string name, object? args = null, string? id = null) =>
        Calls(new FunctionCallContent(id ?? $"call{_script.Count}", name, Args(args)));

    public FakeChatClient Throws(Exception e) => Enqueue(_ => Throwing(e));

    public FakeChatClient TextThenHang(string text) => Enqueue(ct => Hanging(text, ct));

    public FakeChatClient Enqueue(Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> response)
    {
        _script.Enqueue(response);
        return this;
    }

    FakeChatClient Enqueue(ChatResponseUpdate[] updates) => Enqueue(_ => updates.ToAsyncEnumerable());

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        Tools.Add([.. options?.Tools?.Select(t => t.Name) ?? []]);
        if (_script.Count == 0)
            throw new InvalidOperationException("FakeChatClient script is exhausted.");
        return _script.Dequeue()(cancellationToken);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    public static Dictionary<string, object?> Args(object? args) =>
        args is null ? [] : args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));

    static ChatResponseUpdate[] Updates(params AIContent[] contents) =>
        [.. contents.Select(c => new ChatResponseUpdate(ChatRole.Assistant, [c]) { MessageId = "m" })];

    static async IAsyncEnumerable<ChatResponseUpdate> Throwing(Exception e)
    {
        await Task.Yield();
        throw e;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    static async IAsyncEnumerable<ChatResponseUpdate> Hanging(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text) { MessageId = "m" };
        await Task.Delay(Timeout.Infinite, ct);
    }
}
