using System.Diagnostics;
using System.Text;

namespace Anchor.Core;

public enum Answer { No, Yes, Always }

/// <summary>What the user is asked to approve. <paramref name="AlwaysLabel"/> is null when "always" is not offered.</summary>
public sealed record ApprovalRequest(string Title, string? Detail, string? AlwaysLabel);

/// <summary>A question for the user with suggested answers; with <paramref name="AllowOther"/>, the user may type their own.</summary>
public sealed record Question(string Text, IReadOnlyList<string> Options, bool AllowOther);

/// <summary>
/// A tool's change to one file: <paramref name="Before"/> is the content it worked from (null for a file it creates) and
/// <paramref name="After"/> what it wants there (null to delete the file). If the file changes before the write happens,
/// <paramref name="Reapply"/> makes the same change to the new content, or returns null when the change no longer fits;
/// without it, the write is refused.
/// </summary>
public sealed record FileEdit(string Path, string? Before, string? After, Func<string, string?>? Reapply = null);

/// <summary>The person at the other end: approves actions and answers questions.</summary>
public interface IApprover
{
    Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct);

    /// <summary>False when no one can answer (-p), so the ask_user tool isn't offered.</summary>
    bool CanAsk => false;

    /// <summary>The user's answer, or null if they dismissed the question.</summary>
    Task<string?> AskAsync(Question question, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// One prompt at a time: the main agent and its background sub-agents can need the user at once, so each approval or
/// question waits for the one before it. A sub-agent's approvals say which sub-agent is asking.
/// </summary>
public sealed class SerialApprover(IApprover inner) : IApprover
{
    static readonly AsyncLocal<string?> Asker = new();
    readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>For the rest of the calling async flow, approvals are labelled as coming from <paramref name="name"/>.</summary>
    public static void ActFor(string name) => Asker.Value = name;

    public bool CanAsk => inner.CanAsk;

    public Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct) =>
        OneAtATimeAsync(() => inner.ApproveAsync(Asker.Value is { } name ? request with { Title = $"[{name}] {request.Title}" } : request, ct), ct);

    public Task<string?> AskAsync(Question question, CancellationToken ct) => OneAtATimeAsync(() => inner.AskAsync(question, ct), ct);

    async Task<T> OneAtATimeAsync<T>(Func<Task<T>> prompt, CancellationToken ct)
    {
        await _turn.WaitAsync(ct);
        try
        {
            return await prompt();
        }
        finally
        {
            _turn.Release();
        }
    }
}

/// <summary>The only code that lets a tool read outside the workspace, write a file, or run a process: policy, then approval, then the effect.</summary>
/// <param name="saved">Where "always" answers for programs and MCP tools outlive the session; null keeps them in memory only.</param>
public sealed class Gate(Workspace workspace, Policy policy, IApprover approver, Action<AgentEvent> emit, ApprovalStore? saved = null)
{
    public const string Declined = "The user declined this action. Do not retry it; ask the user how to proceed if you are blocked.";

    const string SavedNote = " (saved for this directory)";
    const int UndoDepth = 20;
    static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(10);

    // Background sub-agents use the gate alongside the main agent.
    readonly Lock _lock = new();
    bool _alwaysWrite;
    // "Always" answers for programs and for MCP tools, starting from the ones saved for this workspace.
    readonly (HashSet<string> Programs, HashSet<string> External) _always = Load(saved);
    readonly LinkedList<Dictionary<string, Change>> _turns = [];
    // Each file as the model last saw it, by full path. A sub-agent keeps its own record (ReadSeparately).
    readonly Dictionary<string, Seen> _mainReads = [];
    static readonly AsyncLocal<Dictionary<string, Seen>?> OwnReads = new();
    Dictionary<string, Seen> _read => OwnReads.Value ?? _mainReads;

    long _reads;

    public const int DefaultTrackedFiles = 50;

    /// <summary>How many of the files read most recently are checked for changes around each tool call: each check is a
    /// lookup per file, which adds up on a network share. 0 turns the checks off.</summary>
    public int TrackedFiles { get; init; } = DefaultTrackedFiles;

    // The content's fingerprint, the file's size and time then, the size and time last reported as a change, and when it
    // was read, for TrackedFiles.
    sealed class Seen(string hash, string stamp, long order)
    {
        public string Hash { get; } = hash;
        // The content last known to be on disk (what was read, then each change reported; null once it's deleted), and
        // a settled size and time for it, or "" to compare by content.
        public string? Known { get; set; } = hash;
        public string Stamp { get; set; } = stamp;
        public long Order { get; } = order;
    }

    // A file's content before the turn first changed it (null if the turn created it), and after its latest change.
    sealed record Change(TextFile? Before, string? After);

    public Workspace Workspace => workspace;

    static (HashSet<string>, HashSet<string>) Load(ApprovalStore? saved) =>
        saved?.Load() is { } loaded ? ([.. loaded.Programs], [.. loaded.Tools]) : ([], []);

    /// <summary>"Always" answers saved for this workspace.</summary>
    public ApprovalStore.Saved SavedApprovals => saved?.Load() ?? new();

    /// <summary>Forgets every "always" answer, saved or from this session.</summary>
    public void ForgetApprovals()
    {
        _alwaysWrite = false;
        _always.Programs.Clear();
        _always.External.Clear();
        saved?.Clear();
    }

    /// <summary>
    /// Pre-approves, for this session only, what <c>--allow</c> names: a program, an MCP tool (<c>mcp__server__tool</c>),
    /// or <c>edits</c> for file writes in the workspace. Like answering "always"; denials still apply.
    /// </summary>
    public void Allow(string rule)
    {
        if (rule == "edits")
            _alwaysWrite = true;
        else if (rule.StartsWith("mcp__", StringComparison.Ordinal))
            _always.External.Add(rule);
        else
            _always.Programs.Add(rule);
    }

    /// <summary>Files the current turn has written with the file tools, relative to the workspace when inside it.</summary>
    public List<string> TurnChanges => [.. (_turns.Last?.Value.Keys ?? Enumerable.Empty<string>()).Select(Display).Order(StringComparer.Ordinal)];

    /// <summary>Files written so far; a check loop compares it across a turn to see whether the turn changed anything.</summary>
    public int Writes { get; private set; }

    /// <summary>Resolves a path the tool is about to read.</summary>
    public async Task<string> ReadPathAsync(string? path, CancellationToken ct)
    {
        var (literal, real) = Paths(path);
        await EnforceAsync(policy.Read(literal, real), new ApprovalRequest($"Read outside the workspace: {real}", null, null), false, null, ct);
        return real;
    }

    /// <summary>Resolves a path the tool is about to write; refuses secrets before any content is read.</summary>
    public string WritePath(string? path)
    {
        var (literal, real) = Paths(path);
        if (policy.Write(literal, real) is { Decision: Decision.Deny } v)
            throw new ToolException($"Denied: {v.Reason}.");
        return real;
    }

    /// <summary>Records that the model has seen <paramref name="full"/> as it is now, which replacing the whole file requires.</summary>
    public void MarkRead(string full)
    {
        var text = TextFile.Load(full)?.Text;
        lock (_lock)
            Remember(full, text, knows: true);
    }

    /// <summary>Gives the calling flow, a sub-agent, its own record of what it has read: reading a file for one agent
    /// doesn't let another replace it, and each hears about changes to the files it read.</summary>
    public static void ReadSeparately() => OwnReads.Value = [];

    /// <summary>
    /// Files the calling agent has read that have changed on disk since, through a shell command, another program or the
    /// user, relative to the workspace. Each change is reported once; anchor's own writes for this agent aren't changes.
    /// </summary>
    public IReadOnlyList<string> TakeChanges()
    {
        List<string> changed = [];
        lock (_lock)
            foreach (var (full, seen) in _read.Count <= TrackedFiles ? _read : _read.OrderByDescending(r => r.Value.Order).Take(TrackedFiles))
            {
                var stamp = Stamp(full);
                if (stamp.Length > 0 && stamp == seen.Stamp)
                    continue;
                var hash = stamp.Length > 0 && TextFile.Load(full)?.Text is { } text ? TextFile.Hash(text) : null;
                seen.Stamp = Stamp(full, settled: true);
                // Unchanged, as after a touch or a save without edits, or a change already reported.
                if (hash == seen.Known)
                    continue;
                seen.Known = hash;
                changed.Add(Display(full) + (stamp.Length == 0 ? " (deleted)" : ""));
            }
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    // Size and time, or "" when the file is gone. A settled stamp is also "" while the time is recent: a write in the same
    // clock tick (about 15 ms on Windows) wouldn't move it, so until then the file is compared by content.
    static string Stamp(string full, bool settled = false)
    {
        var info = new FileInfo(full);
        if (!info.Exists || (settled && DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(2)))
            return "";
        return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>Forgets which files the model has read, for when its history is cleared.</summary>
    public void ForgetReads()
    {
        lock (_lock)
            _read.Clear();
    }

    /// <summary>Makes one change to one file; see <see cref="WriteAsync(IReadOnlyList{FileEdit}, CancellationToken)"/>.</summary>
    public async Task<DiffResult> WriteAsync(FileEdit edit, CancellationToken ct) => (await WriteAsync([edit], ct))[0];

    /// <summary>
    /// Makes <paramref name="edits"/>, to distinct files, once the user has seen the diff: one approval for all of them, and
    /// either every file is written or none is. A file that changed since the tool read it gets the change reapplied, or
    /// fails the whole write. Returns the diff of each file as written.
    /// </summary>
    public async Task<IReadOnlyList<DiffResult>> WriteAsync(IReadOnlyList<FileEdit> edits, CancellationToken ct)
    {
        if (edits.Count == 0 || edits.DistinctBy(e => e.Path).Count() != edits.Count)
            throw new ArgumentException("Expected at least one edit, each to a different file.", nameof(edits));
        foreach (var edit in edits)
            RequireRead(edit);

        // One prompt covers every file, so it asks as the strictest of them would: denied, outside, then inside.
        var verdict = edits.Select(e => policy.Write(e.Path, e.Path)).MaxBy(v => v.Decision switch
        {
            Decision.Deny => 3,
            Decision.Ask => v.Reason == Policy.InsideWrite ? 1 : 2,
            _ => 0,
        });
        var detail = edits.Count == 1
            ? Diff.Build(edits[0].Before ?? "", edits[0].After ?? "").Text
            : string.Concat(edits.Select(e => $"{Describe(e)}\n{Diff.Build(e.Before ?? "", e.After ?? "").Text}"));
        var title = edits.Count == 1 ? Describe(edits[0]) : $"Change {edits.Count} files: {string.Join(", ", edits.Select(e => Display(e.Path)))}";
        var inside = verdict.Reason == Policy.InsideWrite;
        await EnforceAsync(verdict, new ApprovalRequest(title, detail, inside ? "all file writes in the workspace" : null),
            inside && _alwaysWrite, inside ? () => _alwaysWrite = true : null, ct);

        List<DiffResult> diffs;
        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
            var plan = edits.Select(Prepare).ToList();
            Commit(plan);
            diffs = [.. plan.Select(p => Diff.Build(p.Current?.Text ?? "", p.After ?? ""))];
            foreach (var (edit, current, after, reapplied) in plan)
            {
                Writes++;
                if (_turns.Last?.Value is { } changes)
                    changes[edit.Path] = changes.TryGetValue(edit.Path, out var first) ? first with { After = after } : new(current, after);
                // The model knows the result when it wrote the whole file, or changed part of a file it had read as it was.
                Remember(edit.Path, after, knows: !reapplied && (edit.Reapply is null || HasSeen(edit.Path, edit.Before)));
            }
        }
        for (var i = 0; i < edits.Count; i++)
            emit(new FileChanged(Display(edits[i].Path), diffs[i].Added, diffs[i].Removed));
        return diffs;
    }

    string Describe(FileEdit edit) => $"{(edit.Before is null ? "Create" : edit.After is null ? "Delete" : "Edit")} {Display(edit.Path)}";

    // Replacing a whole file the model hasn't read, or has read only in an older version, would lose what it hasn't seen.
    void RequireRead(FileEdit edit)
    {
        if (edit is not { Before: not null, After: not null, Reapply: null })
            return;
        bool read, seen;
        lock (_lock)
            (read, seen) = (_read.ContainsKey(edit.Path), HasSeen(edit.Path, edit.Before));
        if (!seen)
            throw new ToolException(read
                ? $"{Display(edit.Path)} has changed since you last read it. Read it again before replacing it."
                : $"{Display(edit.Path)} already exists and you haven't read it. Read it first, so replacing it doesn't lose content you haven't seen.");
    }

    // Under _lock: whether the model last read the file as text.
    bool HasSeen(string full, string? text) => text is not null && _read.TryGetValue(full, out var seen) && seen.Hash == TextFile.Hash(text);

    // What to write to the file now: the edit as approved if the file still holds what the tool read, else the edit reapplied.
    (FileEdit Edit, TextFile? Current, string? After, bool Reapplied) Prepare(FileEdit edit)
    {
        var current = TextFile.Load(edit.Path);
        if (current?.Text == edit.Before)
            return (edit, current, edit.After, false);
        if (current is not null && edit.Before is not null && edit.Reapply?.Invoke(current.Text) is { } after)
            return (edit, current, after, true);
        var what = edit.Before is null ? "was created" : current is null ? "was deleted" : "changed";
        throw new ToolException($"{Display(edit.Path)} {what} after you read it (the user may have edited it), so nothing was written. Read it again, then make the change again.");
    }

    static void Commit(List<(FileEdit Edit, TextFile? Current, string? After, bool Reapplied)> plan)
    {
        var done = 0;
        try
        {
            for (; done < plan.Count; done++)
                Store(plan[done].Edit.Path, plan[done].After, plan[done].Current?.Encoding);
        }
        catch
        {
            // Puts back the files already written. If that fails too, the first error says more.
            foreach (var (edit, current, _, _) in plan.Take(done))
            {
                try
                {
                    Store(edit.Path, current?.Text, current?.Encoding);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            throw;
        }
    }

    static void Store(string full, string? text, Encoding? encoding)
    {
        if (text is null)
            File.Delete(full);
        else
            TextFile.Save(full, text, encoding);
    }

    // Under _lock.
    void Remember(string full, string? text, bool knows)
    {
        if (text is not null && knows)
            _read[full] = new Seen(TextFile.Hash(text), Stamp(full, settled: true), ++_reads);
        else
            _read.Remove(full);
    }

    /// <summary>Starts recording file changes for /undo.</summary>
    public void BeginTurn()
    {
        if (_turns.Last?.Value.Count == 0)
            return;
        _turns.AddLast([]);
        if (_turns.Count > UndoDepth)
            _turns.RemoveFirst();
    }

    /// <summary>Reverts the file-tool changes of the most recent turn that made any. Files changed since are left alone.</summary>
    public (List<string> Restored, List<string> Skipped) Undo()
    {
        while (_turns.Last?.Value.Count == 0)
            _turns.RemoveLast();
        List<string> restored = [], skipped = [];
        if (_turns.Last?.Value is not { } changes)
            return (restored, skipped);
        _turns.RemoveLast();

        foreach (var (full, change) in changes)
        {
            var rel = Display(full);
            if (TextFile.Load(full)?.Text != change.After)
            {
                skipped.Add(rel);
                continue;
            }
            Store(full, change.Before?.Text, change.Before?.Encoding);
            lock (_lock)
                _read.Remove(full);
            restored.Add(rel);
        }
        return (restored, skipped);
    }

    /// <summary>Runs a shell command in the workspace root and returns its masked output.</summary>
    public async Task<string> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        // "Always" is keyed on programs from the bash parse, which is only trustworthy when bash runs the command.
        var programs = policy.ParsesShell ? ShellCommand.Programs(command) : new HashSet<string>();
        // An interpreter can run anything, so "always" for one lasts only this session.
        bool known;
        lock (_lock)
            known = programs.Count > 0 && programs.All(_always.Programs.Contains);
        var save = saved is not null && programs.Count > 0 && !programs.Any(ShellCommand.RunsAnyCode);
        var always = programs.Count > 0 ? $"commands using {string.Join(", ", programs.Order())}{(save ? SavedNote : "")}" : null;
        await EnforceAsync(policy.Run(command), new ApprovalRequest($"Run: {command}", null, always), known, always is null ? null : () =>
        {
            _always.Programs.UnionWith(programs);
            if (save)
                saved!.Add(programs: programs);
        }, ct);

        var (_, output) = await ExecuteAsync(command, timeout, ct);
        return Secrets.Mask(output, workspace);
    }

    /// <summary>
    /// Runs the user's own check command (--until, /until) and reports whether it exited 0. The user wrote it, as with
    /// <c>!cmd</c>, so there is no policy or approval; the output still reaches the model, so it is masked.
    /// </summary>
    public async Task<(bool Passed, string Output)> CheckAsync(string command, int round, CancellationToken ct)
    {
        var (exitCode, output) = await ExecuteAsync(command, CheckTimeout, ct);
        output = Secrets.Mask(output, workspace);
        emit(new CheckRan(command, round, exitCode == 0, output));
        return (exitCode == 0, output);
    }

    async Task<(int? ExitCode, string Output)> ExecuteAsync(string cmd, TimeSpan limit, CancellationToken token)
    {
        var psi = HostShell.StartInfo(cmd, File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
        psi.WorkingDirectory = workspace.Root;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        foreach (var (k, v) in new[] { ("TERM", "dumb"), ("NO_COLOR", "1"), ("PAGER", "cat"), ("GIT_PAGER", "cat"), ("GIT_TERMINAL_PROMPT", "0") })
            psi.Environment[k] = v;

        var output = new ProcessOutput();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Add(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        limitCts.CancelAfter(limit);
        int? exitCode = null;
        string status;
        try
        {
            await process.WaitForExitAsync(limitCts.Token);
            exitCode = process.ExitCode;
            status = $"[exit code {exitCode}]";
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited on its own just now.
            }
            // Waits for it to be gone and its output read, so nothing it printed is lost and nothing outlives the call. A
            // grandchild that left the process tree can hold the output open, so the wait is bounded.
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(drain.Token);
            }
            catch (OperationCanceledException)
            {
            }
            token.ThrowIfCancellationRequested();
            status = $"[timed out after {limit.TotalSeconds:0}s; process killed]";
        }

        return (exitCode, output.IsEmpty ? $"(no output)\n{status}" : $"{output}{status}");
    }

    /// <summary>Calls a tool that lives outside anchor (an MCP server) and returns its masked output.</summary>
    public async Task<string> CallExternalAsync(string tool, bool readOnly, string arguments, Func<CancellationToken, Task<string>> call, CancellationToken ct)
    {
        var detail = arguments.Length > 2_000 ? arguments[..2_000] + " ..." : arguments;
        bool known;
        lock (_lock)
            known = _always.External.Contains(tool);
        await EnforceAsync(policy.External(readOnly), new ApprovalRequest($"Call {tool}", detail, $"all calls to {tool}{(saved is null ? "" : SavedNote)}"),
            known, () =>
            {
                _always.External.Add(tool);
                saved?.Add(tool: tool);
            }, ct);
        return Secrets.Mask(await call(ct), workspace);
    }

    string Display(string full) => workspace.IsInside(full) ? workspace.Relative(full) : full;

    (string Literal, string Real) Paths(string? path)
    {
        var literal = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path, workspace.Root);
        return (literal, workspace.Resolve(path));
    }

    async Task EnforceAsync(Verdict verdict, ApprovalRequest request, bool preapproved, Action? remember, CancellationToken ct)
    {
        switch (verdict.Decision)
        {
            case Decision.Deny:
                throw new ToolException($"Denied: {verdict.Reason}.");
            case Decision.Allow:
                return;
        }

        if (preapproved)
            return;

        var answer = await approver.ApproveAsync(request, ct);
        if (answer == Answer.No)
            throw new ToolException(Declined);
        if (answer == Answer.Always && remember is not null)
            lock (_lock)
                remember();
    }
}
