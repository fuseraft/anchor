using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

public enum MessageKind { Normal, Summary, Note }

/// <summary>Anchor's message metadata and text measures, kept on the message itself so it survives persistence.</summary>
public static class Messages
{
    const string KindKey = "anchor.kind";

    public static ChatMessage Create(MessageKind kind, string text)
    {
        var message = new ChatMessage(ChatRole.User, text);
        (message.AdditionalProperties ??= [])[KindKey] = kind.ToString();
        return message;
    }

    public static MessageKind Kind(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue(KindKey, out var v) == true
        && Enum.TryParse<MessageKind>(v is JsonElement e ? e.GetString() : v?.ToString(), out var kind)
            ? kind
            : MessageKind.Normal;

    /// <summary>A real user input: starts a turn.</summary>
    public static bool IsUserInput(ChatMessage message) => message.Role == ChatRole.User && Kind(message) == MessageKind.Normal;

    public static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        var other => other.ToString() ?? "",
    };

    /// <summary>Rough token count (chars / 4) for when the provider hasn't told us.</summary>
    public static long EstimateTokens(IEnumerable<ChatMessage> messages) => messages.Sum(m => (long)Length(m)) / 4;

    static int Length(ChatMessage message) => message.Contents.Sum(c => c switch
    {
        TextContent t => t.Text.Length,
        FunctionCallContent f => f.Name.Length + JsonSerializer.Serialize(f.Arguments).Length,
        FunctionResultContent r => ResultText(r).Length,
        _ => 0,
    });

    /// <summary>Plain-text transcript for the summarizer; tool results are cut to <paramref name="maxResultChars"/>.</summary>
    public static string Transcript(IEnumerable<ChatMessage> messages, int maxResultChars)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent { Text.Length: > 0 } t when Kind(m) == MessageKind.Summary:
                        sb.Append("EARLIER SUMMARY:\n").Append(t.Text).Append("\n\n");
                        break;
                    case TextContent { Text.Length: > 0 } t when Kind(m) == MessageKind.Note:
                        sb.Append("NOTE: ").Append(t.Text).Append("\n\n");
                        break;
                    case TextContent { Text.Length: > 0 } t:
                        sb.Append(m.Role == ChatRole.User ? "USER: " : "ASSISTANT: ").Append(t.Text).Append("\n\n");
                        break;
                    case FunctionCallContent f:
                        sb.Append($"ASSISTANT called {f.Name}({JsonSerializer.Serialize(f.Arguments)})\n");
                        break;
                    case FunctionResultContent r:
                        var text = ResultText(r);
                        sb.Append("RESULT: ").Append(text.Length > maxResultChars ? text[..maxResultChars] + " [...]" : text).Append("\n\n");
                        break;
                }
            }
        }
        return sb.ToString();
    }
}
