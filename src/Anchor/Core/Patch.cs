namespace Anchor.Core;

/// <summary>
/// The patch format OpenAI's models are trained on (apply_patch): a patch adds, deletes, updates and moves files, and
/// places each change by the lines around it rather than by line numbers.
/// </summary>
public static class Patch
{
    public enum Kind { Add, Delete, Update }

    /// <summary>One file a patch touches: an Add carries the new content, an Update its sections and maybe a new path.</summary>
    public sealed record FileOp(Kind Kind, string Path, string? Content = null, IReadOnlyList<Section>? Sections = null, string? MoveTo = null);

    /// <summary>
    /// Lines that keep (' '), remove ('-') or add ('+') text. They're looked for after the line <paramref name="Anchor"/> names,
    /// when there is one, and at the end of the file when <paramref name="AtEnd"/>.
    /// </summary>
    public sealed record Section(string? Anchor, IReadOnlyList<string> Lines, bool AtEnd);

    const string Begin = "*** Begin Patch", End = "*** End Patch", EndOfFile = "*** End of File";
    const string AddFile = "*** Add File: ", DeleteFile = "*** Delete File: ", UpdateFile = "*** Update File: ", MoveTo = "*** Move to: ";

    // Tried in order, as the reference implementation does: the exact line, then ignoring trailing whitespace, then
    // ignoring the whitespace around it.
    static readonly Func<string, string>[] Loosenings = [s => s, s => s.TrimEnd(), s => s.Trim()];

    /// <summary>The files a patch touches, in order. Throws a ToolException that says what's wrong with a malformed patch.</summary>
    public static List<FileOp> Parse(string patch)
    {
        var lines = patch.ReplaceLineEndings("\n").Split('\n');
        // Only what's between the markers counts, so a patch wrapped in a heredoc or a code fence still works.
        var first = Array.FindIndex(lines, l => l.Trim() == Begin);
        var last = Array.FindLastIndex(lines, l => l.Trim() == End);
        if (first < 0 || last <= first)
            throw new ToolException($"A patch starts with the line '{Begin}' and ends with '{End}'.");

        List<FileOp> ops = [];
        var i = first + 1;
        while (i < last)
        {
            var line = lines[i++];
            if (Header(line, AddFile) is { } added)
            {
                List<string> content = [];
                for (; i < last && !lines[i].StartsWith("*** ", StringComparison.Ordinal); i++)
                    content.Add(lines[i].StartsWith('+')
                        ? lines[i][1..]
                        : throw new ToolException($"Add File {added}: every line of the new file starts with '+', but this one doesn't: {lines[i]}"));
                ops.Add(new(Kind.Add, added, content.Count == 0 ? "" : string.Join('\n', content) + "\n"));
            }
            else if (Header(line, DeleteFile) is { } deleted)
                ops.Add(new(Kind.Delete, deleted));
            else if (Header(line, UpdateFile) is { } updated)
            {
                var moveTo = i < last ? Header(lines[i], MoveTo) : null;
                if (moveTo is not null)
                    i++;
                List<Section> sections = [];
                while (i < last && !IsFileHeader(lines[i]))
                    sections.Add(ReadSection(lines, ref i, last, updated));
                if (sections.Count == 0 && moveTo is null)
                    throw new ToolException($"Update File {updated}: there are no changes after it.");
                ops.Add(new(Kind.Update, updated, Sections: sections, MoveTo: moveTo));
            }
            else if (line.Trim().Length > 0)
                throw new ToolException($"Unexpected line in the patch: {line}. Each file starts with '{AddFile}', '{UpdateFile}' or '{DeleteFile}'.");
        }

        if (ops.Count == 0)
            throw new ToolException("The patch doesn't change any files.");
        if (ops.SelectMany(o => o.MoveTo is null ? [o.Path] : new[] { o.Path, o.MoveTo }).GroupBy(p => p).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new ToolException($"The patch changes {twice.Key} more than once. Put all of a file's changes under one header.");
        return ops;
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="sections"/> applied in order, each found after the one before. Null, with
    /// <paramref name="problem"/> saying why, when a section's lines can't be found.
    /// </summary>
    public static string? Apply(string text, IReadOnlyList<Section> sections, out string? problem)
    {
        problem = null;
        var crlf = text.Contains("\r\n");
        if (crlf)
            text = text.Replace("\r\n", "\n");
        var finalNewline = text.EndsWith('\n');
        List<string> lines = text.Length == 0 ? [] : [.. (finalNewline ? text[..^1] : text).Split('\n')];

        var from = 0;
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            var where = sections.Count > 1 ? $"section {s + 1}: " : "";
            if (section.Anchor is { } anchor)
            {
                var at = Loosenings.Select(loose => lines.FindIndex(from, l => loose(l) == loose(anchor))).FirstOrDefault(i => i >= 0, -1);
                if (at < 0)
                {
                    problem = $"{where}there's no line '{anchor}' to put the change after. Read the file again and copy its lines exactly.";
                    return null;
                }
                from = at + 1;
            }

            List<string> old = [.. section.Lines.Where(l => l[0] != '+').Select(l => l[1..])];
            var start = old.Count == 0 ? (section.AtEnd ? lines.Count : from) : Find(lines, old, from, section.AtEnd);
            if (start < 0)
            {
                problem = $"{where}these lines aren't in the file{(from > 0 ? " after the previous change" : "")}:\n{string.Join('\n', old)}\n" +
                          "Read the file again and copy its lines exactly, with enough context to place the change.";
                return null;
            }

            // Context lines keep the file's own text, so a loose match never rewrites whitespace the patch didn't mean to change.
            List<string> replacement = [];
            var k = start;
            foreach (var line in section.Lines)
            {
                if (line[0] == ' ')
                    replacement.Add(lines[k++]);
                else if (line[0] == '-')
                    k++;
                else
                    replacement.Add(line[1..]);
            }
            lines.RemoveRange(start, old.Count);
            lines.InsertRange(start, replacement);
            from = start + replacement.Count;
        }

        var result = string.Join('\n', lines) + (finalNewline || (lines.Count > 0 && text.Length == 0) ? "\n" : "");
        return crlf ? result.Replace("\n", "\r\n") : result;
    }

    // Where old starts in lines, at or after from (or at the very end, when atEnd and it fits there); -1 if nowhere.
    static int Find(List<string> lines, List<string> old, int from, bool atEnd)
    {
        foreach (var loose in Loosenings)
        {
            if (atEnd && Matches(lines, old, lines.Count - old.Count, loose))
                return lines.Count - old.Count;
            for (var i = from; i + old.Count <= lines.Count; i++)
                if (Matches(lines, old, i, loose))
                    return i;
        }
        return -1;
    }

    static bool Matches(List<string> lines, List<string> old, int at, Func<string, string> loose)
    {
        if (at < 0)
            return false;
        for (var j = 0; j < old.Count; j++)
            if (loose(lines[at + j]) != loose(old[j]))
                return false;
        return true;
    }

    static Section ReadSection(string[] lines, ref int i, int last, string path)
    {
        string? anchor = null;
        if (lines[i].StartsWith("@@", StringComparison.Ordinal))
        {
            anchor = lines[i][2..].Trim() is { Length: > 0 } named ? named : null;
            i++;
        }
        List<string> body = [];
        for (; i < last && !lines[i].StartsWith("@@", StringComparison.Ordinal) && !lines[i].StartsWith("*** ", StringComparison.Ordinal); i++)
        {
            // A blank line in a section is a blank context line whose leading space was dropped.
            var line = lines[i].Length == 0 ? " " : lines[i];
            body.Add(line[0] is ' ' or '-' or '+'
                ? line
                : throw new ToolException($"Update File {path}: each line of a change starts with ' ', '-' or '+', but this one doesn't: {line}"));
        }
        var atEnd = i < last && lines[i].Trim() == EndOfFile;
        if (atEnd)
            i++;
        if (body.Count == 0)
            throw new ToolException(i < last && !atEnd && !IsFileHeader(lines[i]) && !lines[i].StartsWith("@@", StringComparison.Ordinal)
                ? $"Unexpected line in the patch: {lines[i]}"
                : $"Update File {path}: a section has no lines.");
        return new(anchor, body, atEnd);
    }

    static bool IsFileHeader(string line) =>
        line.StartsWith(AddFile, StringComparison.Ordinal) || line.StartsWith(DeleteFile, StringComparison.Ordinal) || line.StartsWith(UpdateFile, StringComparison.Ordinal);

    static string? Header(string line, string prefix) =>
        !line.StartsWith(prefix, StringComparison.Ordinal) ? null
        : line[prefix.Length..].Trim() is { Length: > 0 } path ? path
        : throw new ToolException($"'{prefix.Trim()}' needs a path after it.");
}
