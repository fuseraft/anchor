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

    bool _alwaysWrite;
    readonly HashSet<string> _alwaysPrograms = [];

    public Workspace Workspace => workspace;

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
        emit(new FileChanged(rel, diff.Added, diff.Removed));
        return diff;
    }

    /// <summary>Runs a shell command in the workspace root and returns its masked output.</summary>
    public async Task<string> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var programs = ShellCommand.Programs(command);
        var known = programs.Count > 0 && programs.All(_alwaysPrograms.Contains);
        var always = programs.Count > 0 ? $"commands using {string.Join(", ", programs.Order())}" : null;
        await EnforceAsync(policy.Run(command), new ApprovalRequest($"Run: {command}", null, always),
            known, always is null ? null : () => _alwaysPrograms.UnionWith(programs), ct);

        var result = await ExecuteAsync(command, timeout, ct);
        return Secrets.Mask(result, workspace);

        async Task<string> ExecuteAsync(string cmd, TimeSpan limit, CancellationToken token)
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
            string status;
            try
            {
                await process.WaitForExitAsync(limitCts.Token);
                status = $"[exit code {process.ExitCode}]";
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                token.ThrowIfCancellationRequested();
                status = $"[timed out after {limit.TotalSeconds:0}s; process killed]";
            }

            lock (output)
                return output.Length == 0 ? $"(no output)\n{status}" : $"{output}{status}";
        }
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

        var answer = await approver.ApproveAsync(request, ct);
        if (answer == Answer.No)
            throw new ToolException(Declined);
        if (answer == Answer.Always)
            remember?.Invoke();
    }
}
