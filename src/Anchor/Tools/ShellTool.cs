using System.ComponentModel;
using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tools;

public sealed class ShellTool(Gate gate)
{
    const int MaxTimeoutSeconds = 600;

    public IEnumerable<AIFunction> All() => [AIFunctionFactory.Create(Shell, new AIFunctionFactoryOptions
    {
        Name = "shell",
        Description = $"Run a {HostShell.Name} command in the workspace root and return its combined output and exit code. " +
                      "Non-interactive: stdin is closed. Don't start servers or background processes; they are killed at the timeout.",
    })];

    public Task<string> Shell(
        [Description("The command to run.")] string command,
        [Description("Timeout in seconds (max 600).")] int timeout_seconds = 120,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ToolException("command is empty.");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, MaxTimeoutSeconds));
        return gate.RunAsync(command, timeout, ct);
    }
}
