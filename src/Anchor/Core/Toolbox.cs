using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>The tools offered to the model, and dispatch of their calls.</summary>
public sealed class Toolbox(IEnumerable<AIFunction> tools)
{
    public const int MaxResultChars = 30_000;

    readonly Dictionary<string, AIFunction> _tools = tools.ToDictionary(t => t.Name);

    public IList<AITool> Declarations => [.. _tools.Values];

    public async Task<(string Text, bool Ok)> InvokeAsync(FunctionCallContent call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return ($"Error: unknown tool '{call.Name}'.", false);

        try
        {
            var result = await tool.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
            return (Cap(Stringify(result)), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return ($"Error: {e.Message}", false);
        }
    }

    public static string Summarize(FunctionCallContent call)
    {
        var first = call.Arguments?.Values.FirstOrDefault(v => v is not null);
        var text = first switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            null => "",
            _ => first.ToString(),
        } ?? "";
        text = text.ReplaceLineEndings(" ");
        return text.Length > 80 ? text[..77] + "..." : text;
    }

    static string Stringify(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        _ => JsonSerializer.Serialize(result),
    };

    static string Cap(string text)
    {
        if (text.Length <= MaxResultChars)
            return text;
        var half = MaxResultChars / 2;
        return $"{text[..half]}\n\n[... {text.Length - MaxResultChars} characters omitted ...]\n\n{text[^half..]}";
    }
}
