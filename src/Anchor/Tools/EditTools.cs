using System.ComponentModel;
using System.Text.RegularExpressions;
using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tools;

/// <summary>write_file and edit_file. Both show the user a diff before anything touches disk.</summary>
public sealed partial class EditTools(Gate gate)
{
    public IEnumerable<AIFunction> All() =>
    [
        AIFunctionFactory.Create(WriteFile, "write_file"),
        AIFunctionFactory.Create(EditFile, "edit_file"),
    ];

    [Description("Create a file or replace its entire content. For changes to an existing file, prefer edit_file.")]
    public async Task<string> WriteFile(
        [Description("File path, relative to the workspace root.")] string path,
        [Description("The complete file content.")] string content,
        CancellationToken ct = default)
    {
        var full = gate.WritePath(path);
        if (Directory.Exists(full))
            throw new ToolException($"'{path}' is a directory.");

        var before = TextFile.Load(full)?.Text;
        if (before is not null && Elided().Match(content) is { Success: true } m && !before.Contains(m.Value.Trim()))
            throw new ToolException($"The content looks elided (\"{m.Value.Trim()}\"). Write the complete file, or use edit_file to change part of it.");

        var diff = await gate.WriteAsync(new FileEdit(full, before, content), ct);
        var rel = gate.Workspace.Relative(full);
        return before is null
            ? $"Created {rel} ({content.Split('\n').Length} lines)."
            : $"Wrote {rel} (+{diff.Added} -{diff.Removed}).";
    }

    [Description("Replace exact text in a file. old_string must match the file exactly, including indentation, and be unique unless replace_all is true. Read the file first.")]
    public async Task<string> EditFile(
        [Description("File path, relative to the workspace root.")] string path,
        [Description("The exact text to replace.")] string old_string,
        [Description("The replacement text.")] string new_string,
        [Description("Replace every occurrence instead of requiring a unique match.")] bool replace_all = false,
        CancellationToken ct = default)
    {
        if (old_string.Length == 0)
            throw new ToolException("old_string is empty. Use write_file to create a file.");
        if (old_string == new_string)
            throw new ToolException("old_string and new_string are identical.");

        var full = await gate.ReadPathAsync(path, ct);
        gate.WritePath(path);
        if (TextFile.Load(full) is not { Text: var before })
            throw new ToolException($"'{path}' does not exist. Use write_file to create it.");

        if (Replace(before, old_string, new_string, replace_all, out var starts) is not { } after)
            throw new ToolException(starts.Count == 0
                ? NotFound(before, old_string)
                : $"old_string matches {starts.Count} places (lines {string.Join(", ", starts.Take(10).Select(s => LineOf(before, s)))}{(starts.Count > 10 ? ", ..." : "")}). " +
                  "Include more surrounding lines to make it unique, or set replace_all.");

        // If the file changes while the user looks at the diff, the same replacement is made in the new content, as long as
        // old_string still appears as many times.
        var diff = await gate.WriteAsync(new FileEdit(full, before, after,
            current => Replace(current, old_string, new_string, replace_all, out var found) is { } redone && found.Count == starts.Count ? redone : null), ct);
        var rel = gate.Workspace.Relative(full);
        return starts.Count > 1
            ? $"Replaced {starts.Count} occurrences in {rel} (+{diff.Added} -{diff.Removed})."
            : $"Edited {rel} at line {LineOf(before, starts[0])} (+{diff.Added} -{diff.Removed}).";
    }

    // text with old replaced by new, both given text's line endings; starts is where old was found. Null when old isn't
    // found, or is found more than once without replaceAll.
    static string? Replace(string text, string old, string @new, bool replaceAll, out List<int> starts)
    {
        if (text.Contains("\r\n"))
        {
            old = old.ReplaceLineEndings("\r\n");
            @new = @new.ReplaceLineEndings("\r\n");
        }
        starts = Occurrences(text, old);
        return starts.Count == 1 || (starts.Count > 1 && replaceAll) ? text.Replace(old, @new, StringComparison.Ordinal) : null;
    }

    static List<int> Occurrences(string text, string value)
    {
        var found = new List<int>();
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            found.Add(i);
        return found;
    }

    static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    static string NotFound(string text, string old)
    {
        var first = old.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        var lines = text.Split('\n');
        var similar = first is null ? [] : Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == first).Take(5).Select(i => i + 1).ToList();
        return similar.Count > 0
            ? $"old_string was not found exactly. Its first line appears (ignoring indentation) at line {string.Join(", ", similar)}; re-read that range and copy the text exactly."
            : "old_string was not found. Re-read the file and copy the text exactly.";
    }

    [GeneratedRegex(@"(?im)^[ \t]*(//|#|--|/\*|\*|<!--)?[ \t]*(\.\.\.|…).*\b(rest|existing|remaining|unchanged|same as before|previous)\b.*$")]
    private static partial Regex Elided();
}
