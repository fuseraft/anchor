using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>The tools offered to the model, and dispatch of their calls. Tools can be added later (MCP servers connect in the background).</summary>
public sealed class Toolbox(IEnumerable<AIFunction> tools)
{
    public const int MaxResultChars = 30_000;

    readonly Dictionary<string, AIFunction> _tools = tools.ToDictionary(t => t.Name);
    readonly Lock _lock = new();

    /// <summary>
    /// Tools that edit files, in two formats: apply_patch, which OpenAI's models are trained on, and write_file with edit_file,
    /// which other models are. A model is offered one format. A call in the other still runs, as after switching models.
    /// </summary>
    public static readonly string[] PatchTools = ["apply_patch"], ReplaceTools = ["write_file", "edit_file"];

    /// <summary>The tools to offer <paramref name="model"/>: all of them, less the edit tools in the format it isn't trained on.</summary>
    public IList<AITool> DeclarationsFor(string? model)
    {
        var (preferred, other) = UsesPatches(model) ? (PatchTools, ReplaceTools) : (ReplaceTools, PatchTools);
        lock (_lock)
        {
            // Only when the preferred format is here, so a toolbox with one format never loses its edit tools.
            var hidden = _tools.Keys.Any(preferred.Contains) ? other : [];
            return [.. _tools.Values.Where(t => !hidden.Contains(t.Name))];
        }
    }

    /// <summary>Whether <paramref name="model"/> is one of OpenAI's (GPT, Codex, o-series), which edit files with apply_patch.</summary>
    public static bool UsesPatches(string? model)
    {
        // A router may put the vendor first, as in openai/gpt-5.
        var name = (model?[(model.LastIndexOf('/') + 1)..] ?? "").ToLowerInvariant();
        return name.Contains("gpt") || name.Contains("codex") || (name.Length > 1 && name[0] == 'o' && char.IsAsciiDigit(name[1]));
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

    /// <summary>A new toolbox with only the named tools that exist right now. Naming any edit tool brings both formats, so
    /// whichever model runs it gets the one it's trained on.</summary>
    public Toolbox Subset(IEnumerable<string> names)
    {
        var wanted = names.ToList();
        if (wanted.Any(n => PatchTools.Contains(n) || ReplaceTools.Contains(n)))
            wanted = [.. wanted, .. PatchTools, .. ReplaceTools];
        lock (_lock)
            return new Toolbox(wanted.Select(n => _tools.GetValueOrDefault(n)).OfType<AIFunction>().Distinct());
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
        var (name, first) = call.Arguments?.FirstOrDefault(a => a.Value is not null) ?? default;
        var text = first switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            // A list reads as its size ("3 tasks"); a single item as the item itself.
            JsonElement { ValueKind: JsonValueKind.Array } e when e.GetArrayLength() == 1 => Summarize(new(call.CallId, call.Name, new Dictionary<string, object?> { [name] = e[0] })),
            JsonElement { ValueKind: JsonValueKind.Array } e => $"{e.GetArrayLength()} {name}",
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
