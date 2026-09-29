using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>Runs one task in a fresh agent with its own context, through the same gate, and returns only its final report.</summary>
public sealed class SubAgentRunner(Toolbox toolbox, string systemPrompt, Action<AgentEvent> emit,
    Func<string?, (IChatClient Client, ChatOptions Options)> clientFor, Func<Compactor>? compactor = null)
{
    public static readonly string[] ReadOnlyTools = ["read_file", "list_dir", "glob", "grep", "skill"];

    const string Instructions = """


        # You are a sub-agent
        Another agent gave you the task below. Work on it with your tools, then reply with a concise final report.
        Only that final message reaches the other agent, so include every finding, path and detail it needs.
        You cannot ask questions; if something is ambiguous, make a reasonable choice and say so in the report.
        """;

    public async Task<string> RunAsync(string task, AgentDefinition? definition, CancellationToken ct)
    {
        var name = definition?.Name ?? "agent";
        var tools = toolbox.Subset((definition?.Tools ?? ReadOnlyTools).Where(t => t != "agent"));
        var (client, options) = clientFor(definition?.Model);
        var prompt = systemPrompt + Instructions + (definition is null ? "" : $"\n\n{definition.Prompt}");

        string? detail = null;
        var agent = new Agent(client, tools, prompt, e =>
        {
            if (e is TurnEnded ended)
                detail = ended.Detail;
            emit(new SubAgentEvent(name, e));
        }, options, compactor: compactor?.Invoke());

        var end = await agent.RunTurnAsync(task, ct);
        ct.ThrowIfCancellationRequested();

        var report = agent.History.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Text.Length > 0)?.Text.Trim() ?? "";
        return end == TurnEnd.Completed
            ? report.Length > 0 ? report : $"[sub-agent {name} finished without a report]"
            : $"[sub-agent {name} stopped early: {detail ?? end.ToString()}]\n\n{report}".TrimEnd();
    }
}
