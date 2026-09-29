using System.Diagnostics;
using System.Text;

namespace Anchor.Core;

public enum Answer { No, Yes, Always }

/// <summary>What the user is asked to approve. <paramref name="AlwaysLabel"/> is null when "always" is not offered.</summary>
public sealed record ApprovalRequest(string Title, string? Detail, string? AlwaysLabel);

public interface IApprover
{
    Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>The only code that lets a tool read outside the workspace, write a file, or run a process: policy, then approval, then the effect.</summary>
public sealed class Gate(Workspace workspace, Policy policy, IApprover approver, Action<AgentEvent> emit)
{
    public const string Declined = "The user declined this action. Do not retry it; ask the user how to proceed if you are blocked.";

    const int UndoDepth = 20;
    static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(10);
    static readonly AsyncLocal<string?> Unaskable = new();

    bool _alwaysWrite;
    readonly HashSet<string> _alwaysPrograms = [];
    readonly HashSet<string> _alwaysExternal = [];
    readonly LinkedList<Dictionary<string, (string? Before, string After)>> _turns = [];

    public Workspace Workspace => workspace;

    /// <summary>
    /// For the rest of the calling async flow, anything that would ask the user is refused instead. Parallel sub-agents
    /// run this way, so two approval prompts never appear at once. The caller's own flow is unaffected.
    /// </summary>
    public static void RefuseAsking(string reason) => Unaskable.Value = reason;

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

    /// <summary>Writes <paramref name="after"/> to <paramref name="full"/> once the user has seen the diff.</summary>
    public async Task<DiffResult> WriteAsync(string full, string? before, string after, CancellationToken ct)
    {
        var diff = Diff.Build(before ?? "", after);
        var verdict = policy.Write(full, full);
        var rel = workspace.IsInside(full) ? workspace.Relative(full) : full;
        var title = before is null ? $"Create {rel}" : $"Edit {rel}";
        var inside = verdict.Reason == Policy.InsideWrite;
        await EnforceAsync(verdict, new ApprovalRequest(title, diff.Text, inside ? "all file writes in the workspace" : null),
            inside && _alwaysWrite, inside ? () => _alwaysWrite = true : null, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, after, ct);
        Writes++;
        if (_turns.Last?.Value is { } changes)
            changes[full] = (changes.TryGetValue(full, out var first) ? first.Before : before, after);
        emit(new FileChanged(rel, diff.Added, diff.Removed));
        return diff;
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

        foreach (var (full, (before, after)) in changes)
        {
            var rel = workspace.IsInside(full) ? workspace.Relative(full) : full;
            var current = File.Exists(full) ? File.ReadAllText(full) : null;
            if (current != after)
            {
                skipped.Add(rel);
                continue;
            }
            if (before is null)
                File.Delete(full);
            else
                File.WriteAllText(full, before);
            restored.Add(rel);
        }
        return (restored, skipped);
    }

    /// <summary>Runs a shell command in the workspace root and returns its masked output.</summary>
    public async Task<string> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        // "Always" is keyed on programs from the bash parse, which is only trustworthy when bash runs the command.
        var programs = policy.ParsesShell ? ShellCommand.Programs(command) : new HashSet<string>();
        var known = programs.Count > 0 && programs.All(_alwaysPrograms.Contains);
        var always = programs.Count > 0 ? $"commands using {string.Join(", ", programs.Order())}" : null;
        await EnforceAsync(policy.Run(command), new ApprovalRequest($"Run: {command}", null, always),
            known, always is null ? null : () => _alwaysPrograms.UnionWith(programs), ct);

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
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", cmd])
            : new ProcessStartInfo(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh", ["-c", cmd]);
        psi.WorkingDirectory = workspace.Root;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        foreach (var (k, v) in new[] { ("TERM", "dumb"), ("NO_COLOR", "1"), ("PAGER", "cat"), ("GIT_PAGER", "cat"), ("GIT_TERMINAL_PROMPT", "0") })
            psi.Environment[k] = v;

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Append(e.Data).Append('\n'); };
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
            process.Kill(entireProcessTree: true);
            token.ThrowIfCancellationRequested();
            status = $"[timed out after {limit.TotalSeconds:0}s; process killed]";
        }

        lock (output)
            return (exitCode, output.Length == 0 ? $"(no output)\n{status}" : $"{output}{status}");
    }

    /// <summary>Calls a tool that lives outside anchor (an MCP server) and returns its masked output.</summary>
    public async Task<string> CallExternalAsync(string tool, bool readOnly, string arguments, Func<CancellationToken, Task<string>> call, CancellationToken ct)
    {
        var detail = arguments.Length > 2_000 ? arguments[..2_000] + " ..." : arguments;
        await EnforceAsync(policy.External(readOnly), new ApprovalRequest($"Call {tool}", detail, $"all calls to {tool}"),
            _alwaysExternal.Contains(tool), () => _alwaysExternal.Add(tool), ct);
        return Secrets.Mask(await call(ct), workspace);
    }

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
        if (Unaskable.Value is { } reason)
            throw new ToolException($"Not allowed without approval, and {reason}. Report what you couldn't do instead.");

        var answer = await approver.ApproveAsync(request, ct);
        if (answer == Answer.No)
            throw new ToolException(Declined);
        if (answer == Answer.Always)
            remember?.Invoke();
    }
}
