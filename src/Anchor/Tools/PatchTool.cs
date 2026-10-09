using System.ComponentModel;
using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tools;

/// <summary>apply_patch: adds, changes, deletes and moves files with the patch format OpenAI's models are trained on.</summary>
public sealed class PatchTool(Gate gate)
{
    public IEnumerable<AIFunction> All() => [AIFunctionFactory.Create(ApplyPatch, "apply_patch")];

    [Description("""
        Edit files with a patch. One patch can add, update, delete and move several files, and the user approves it as a whole.

        *** Begin Patch
        *** Update File: src/app.py
        @@ def greet():
         def greet():
        -    print("hi")
        +    print("hello")
        *** Add File: src/new.py
        +print("new file")
        *** Delete File: src/old.py
        *** End Patch

        Update File: each change is a few unchanged lines prefixed with a space for context, then '-' lines to remove and
        '+' lines to add. Changes are found by their context, never by line numbers, so copy the context exactly and use
        about three lines of it. Start a change with '@@' and the line of the class or function it's in when the context
        alone appears more than once. Changes to one file go in file order. Add '*** End of File' after a change that ends
        the file. Put '*** Move to: <path>' right after an Update File line to rename the file.

        Add File: every line of the new file starts with '+'. Paths are relative to the workspace root. Read a file before
        updating it.
        """)]
    public async Task<string> ApplyPatch(
        [Description("The whole patch, from '*** Begin Patch' to '*** End Patch'.")] string input,
        CancellationToken ct = default)
    {
        var ops = Patch.Parse(input);
        List<FileEdit> edits = [];
        List<(string Label, int Edit)> changed = []; // each summary line, and the edit whose diff it reports (-1: none)
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case Patch.Kind.Add:
                    var created = gate.WritePath(op.Path);
                    if (File.Exists(created) || Directory.Exists(created))
                        throw new ToolException($"Add File {op.Path}: it already exists. Change it with Update File.");
                    changed.Add(($"A {op.Path}", edits.Count));
                    edits.Add(new(created, null, op.Content));
                    break;

                case Patch.Kind.Delete:
                    var deleted = await ExistingAsync(op, ct);
                    changed.Add(($"D {op.Path}", edits.Count));
                    edits.Add(new(deleted.Path, deleted.Text, null));
                    break;

                case Patch.Kind.Update:
                    var (full, before) = await ExistingAsync(op, ct);
                    if (Patch.Apply(before, op.Sections!, out var problem) is not { } after)
                        throw new ToolException($"Update File {op.Path}: {problem}");
                    if (op.MoveTo is { } moveTo)
                    {
                        var target = gate.WritePath(moveTo);
                        if (File.Exists(target) || Directory.Exists(target))
                            throw new ToolException($"Move to {moveTo}: it already exists.");
                        // A move is the old path deleted and the new one created; its line counts compare the two contents.
                        var moved = Diff.Build(before, after);
                        changed.Add(($"M {op.Path} -> {moveTo} (+{moved.Added} -{moved.Removed})", -1));
                        edits.Add(new(full, before, null));
                        edits.Add(new(target, null, after));
                    }
                    else
                    {
                        // If the file changes while the user looks at the diff, the patch is applied to the new content instead.
                        changed.Add(($"M {op.Path}", edits.Count));
                        edits.Add(new(full, before, after, current => Patch.Apply(current, op.Sections!, out _)));
                    }
                    break;
            }
        }
        if (edits.DistinctBy(e => e.Path).Count() != edits.Count)
            throw new ToolException("The patch changes the same file more than once, under different paths. Put all of a file's changes under one header.");

        var diffs = await gate.WriteAsync(edits, ct);
        return "Success. Updated the following files:\n" + string.Join('\n', changed.Select(c => c.Edit < 0 ? c.Label : $"{c.Label} (+{diffs[c.Edit].Added} -{diffs[c.Edit].Removed})"));
    }

    async Task<(string Path, string Text)> ExistingAsync(Patch.FileOp op, CancellationToken ct)
    {
        var full = await gate.ReadPathAsync(op.Path, ct);
        gate.WritePath(op.Path);
        return TextFile.Load(full) is { Text: var text }
            ? (full, text)
            : throw new ToolException($"{op.Kind} File {op.Path}: there's no such file.{(op.Kind == Patch.Kind.Update ? " Create it with Add File." : "")}");
    }
}
