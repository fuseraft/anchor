using System.ComponentModel;
using System.Text;
using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tools;

/// <summary>The agent tools (start a background sub-agent, check on it, stop it) and the skill tool (load a skill's instructions).</summary>
public sealed class AgentTools(SubAgentRunner runner, IReadOnlyList<AgentDefinition> agents, IReadOnlyList<Skill> skills)
{
    const int MaxListedFiles = 50;

    public IEnumerable<AIFunction> All()
    {
        var agentList = string.Concat(agents.Select(a => $"\n- {a.Name}: {a.Description}"));
        yield return AIFunctionFactory.Create(RunAgent, new AIFunctionFactoryOptions
        {
            Name = "agent",
            Description = "Start a sub-agent in the background on a self-contained task, with its own fresh context, and get its id. " +
                          "Only its final report comes back, as a message when it finishes, so put everything it needs in the task. " +
                          "Use it for broad searches and investigations that would otherwise fill your context. " +
                          $"Up to {SubAgentRunner.MaxRunning} can run at once, so start one per part when a question splits into separate parts. " +
                          "While they run, keep working or end your reply: the turn waits for every report. " +
                          "Without a name, the sub-agent can only read." +
                          (agentList.Length > 0 ? $"\nNamed agents:{agentList}" : ""),
        });
        yield return AIFunctionFactory.Create(AgentStatus, "agent_status");
        yield return AIFunctionFactory.Create(StopAgent, "agent_stop");
        if (skills.Count > 0)
            yield return AIFunctionFactory.Create(LoadSkill, "skill");
    }

    public string RunAgent(
        [Description("The complete task, with all the context the sub-agent needs.")] string task,
        [Description("Name of a named agent to use; omit for the default read-only agent.")] string? agent = null,
        CancellationToken ct = default)
    {
        var definition = agent is null ? null
            : agents.FirstOrDefault(a => a.Name == agent)
              ?? throw new ToolException($"Unknown agent '{agent}'. Available: {(agents.Count == 0 ? "none" : string.Join(", ", agents.Select(a => a.Name)))}.");
        var id = runner.Start(task, definition, ct);
        return $"Started {id} in the background. Its report will arrive as a message when it finishes.";
    }

    [Description("Check on this turn's sub-agents: whether each is running or done, for how long, how many tool calls it has made, " +
                 "and its latest ones. Use it when the user asks how they're doing.")]
    public string AgentStatus([Description("A sub-agent's id; omit for all of them.")] string? id = null) => runner.Status(id);

    [Description("Stop a running sub-agent, such as one that is no longer needed or has gone off track. Its report won't arrive.")]
    public Task<string> StopAgent([Description("The sub-agent's id.")] string id) => runner.StopAsync(id);

    [Description("Load a skill's full instructions by name. Load a skill whenever the task matches its description, before starting the work.")]
    public string LoadSkill([Description("The skill name.")] string name)
    {
        var skill = skills.FirstOrDefault(s => s.Name == name)
            ?? throw new ToolException($"Unknown skill '{name}'. Available: {string.Join(", ", skills.Select(s => s.Name))}.");
        var (_, body) = Definitions.Frontmatter(File.ReadAllText(Path.Combine(skill.Directory, "SKILL.md")));

        var files = Directory.EnumerateFiles(skill.Directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skill.Directory, f).Replace('\\', '/'))
            .Where(f => f != "SKILL.md" && !Secrets.IsSecretPath(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder($"# Skill: {skill.Name}\nDirectory: {skill.Directory}\n");
        if (files.Count > 0)
            sb.Append($"Files (read them with read_file; run scripts with shell): {string.Join(", ", files.Take(MaxListedFiles))}{(files.Count > MaxListedFiles ? ", ..." : "")}\n");
        return sb.Append('\n').Append(body.Trim()).ToString();
    }
}
