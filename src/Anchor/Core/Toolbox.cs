using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>The tools offered to the model, and dispatch of their calls. Tools can be added later (MCP servers connect in the background).</summary>
public sealed class Toolbox(IEnumerable<AIFunction> tools)
{
    public const int MaxResultChars = 30_000;

    readonly Dictionary<string, AIFunction> _tools = tools.ToDictionary(t => t.Name);
    readonly Lock _lock = new();

    public IList<AITool> Declarations
    {
        get { lock (_lock) return [.. _tools.Values]; }
    }

    public IReadOnlyList<string> Names
    {
        get { lock (_lock) return [.. _tools.Keys.Order(StringComparer.Ordinal)]; }
    }

    public void Add(IEnumerable<AIFunction> more)
    {
        lock (_lock)
            foreach (var tool in more)
                _tools[tool.Name] = tool;
    }

    public void Remove(Func<string, bool> match)
    {
        lock (_lock)
            foreach (var name in _tools.Keys.Where(match).ToList())
                _tools.Remove(name);
    }

    /// <summary>A new toolbox with only the named tools that exist right now.</summary>
    public Toolbox Subset(IEnumerable<string> names)
    {
        lock (_lock)
            return new Toolbox(names.Select(n => _tools.GetValueOrDefault(n)).OfType<AIFunction>().Distinct());
    }

    public async Task<(string Text, bool Ok)> InvokeAsync(FunctionCallContent call, CancellationToken ct)
    {
        AIFunction? tool;
        lock (_lock)
            _tools.TryGetValue(call.Name, out tool);
        if (tool is null)
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
