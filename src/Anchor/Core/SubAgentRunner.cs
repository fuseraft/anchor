using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>Runs one task in a fresh agent with its own context, through the same gate, and returns only its final report.</summary>
public sealed class SubAgentRunner(Toolbox toolbox, string systemPrompt, Action<AgentEvent> emit,
    Func<string?, (IChatClient Client, ChatOptions Options)> clientFor, Func<Compactor>? compactor = null, int? maxRounds = null)
{
    public static readonly string[] ReadOnlyTools = ["read_file", "list_dir", "glob", "grep", "skill"];

    public const int MaxParallel = 4;

    const string Instructions = """


        # You are a sub-agent
        Another agent gave you the task below. Work on it with your tools, then reply with a concise final report.
        Only that final message reaches the other agent, so include every finding, path and detail it needs.
        You cannot ask questions; if something is ambiguous, make a reasonable choice and say so in the report.
        """;

    /// <summary>Runs default read-only sub-agents at the same time, one per task, and returns every report in order.</summary>
    public async Task<string> RunParallelAsync(IReadOnlyList<string> tasks, CancellationToken ct)
    {
        var reports = await Task.WhenAll(tasks.Select((task, i) => Task.Run(async () =>
        {
            Gate.RefuseAsking("sub-agents running in parallel can't ask the user");
            return await RunAsync(task, null, ct, $"agent {i + 1}");
        }, ct)));
        return string.Join("\n\n", reports.Select((report, i) => $"## Task {i + 1}\n{report}"));
    }

    public async Task<string> RunAsync(string task, AgentDefinition? definition, CancellationToken ct, string? label = null)
    {
        var name = label ?? definition?.Name ?? "agent";
        var tools = toolbox.Subset((definition?.Tools ?? ReadOnlyTools).Where(t => t is not ("agent" or "agents")));
        var (client, options) = clientFor(definition?.Model);
        var prompt = systemPrompt + Instructions + (definition is null ? "" : $"\n\n{definition.Prompt}");

        string? detail = null;
        var agent = new Agent(client, tools, prompt, e =>
        {
            if (e is TurnEnded ended)
                detail = ended.Detail;
            emit(new SubAgentEvent(name, e));
        }, options, compactor: compactor?.Invoke())
        {
            MaxRounds = maxRounds,
        };

        var end = await agent.RunTurnAsync(task, ct);
        ct.ThrowIfCancellationRequested();

        var report = agent.History.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Text.Length > 0)?.Text.Trim() ?? "";
        return end == TurnEnd.Completed
            ? report.Length > 0 ? report : $"[sub-agent {name} finished without a report]"
            : $"[sub-agent {name} stopped early: {detail ?? end.ToString()}]\n\n{report}".TrimEnd();
    }
}
