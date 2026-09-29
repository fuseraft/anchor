using System.ComponentModel;
using System.Text;
using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tools;

/// <summary>The agent tool (delegate to a sub-agent) and the skill tool (load a skill's instructions).</summary>
public sealed class AgentTools(SubAgentRunner runner, IReadOnlyList<AgentDefinition> agents, IReadOnlyList<Skill> skills)
{
    const int MaxListedFiles = 50;

    public IEnumerable<AIFunction> All()
    {
        var agentList = string.Concat(agents.Select(a => $"\n- {a.Name}: {a.Description}"));
        yield return AIFunctionFactory.Create(RunAgent, new AIFunctionFactoryOptions
        {
            Name = "agent",
            Description = "Delegate a self-contained task to a sub-agent with its own fresh context. It returns only its final report, " +
                          "so put everything it needs in the task. Use it for broad searches and investigations that would otherwise fill your context. " +
                          "Without a name, the sub-agent can only read." +
                          (agentList.Length > 0 ? $"\nNamed agents:{agentList}" : ""),
        });
        yield return AIFunctionFactory.Create(RunAgents, new AIFunctionFactoryOptions
        {
            Name = "agents",
            Description = $"Run up to {SubAgentRunner.MaxParallel} independent investigations at the same time, each in its own read-only sub-agent, " +
                          "and get every report back. Use it when a question splits into separate parts, such as one module each. " +
                          "Use agent instead for a single task or a named agent.",
        });
        if (skills.Count > 0)
            yield return AIFunctionFactory.Create(LoadSkill, "skill");
    }

    public async Task<string> RunAgent(
        [Description("The complete task, with all the context the sub-agent needs.")] string task,
        [Description("Name of a named agent to use; omit for the default read-only agent.")] string? agent = null,
        CancellationToken ct = default)
    {
        var definition = agent is null ? null
            : agents.FirstOrDefault(a => a.Name == agent)
              ?? throw new ToolException($"Unknown agent '{agent}'. Available: {(agents.Count == 0 ? "none" : string.Join(", ", agents.Select(a => a.Name)))}.");
        return await runner.RunAsync(task, definition, ct);
    }

    public Task<string> RunAgents(
        [Description("One complete, self-contained task per sub-agent, with all the context it needs.")] string[] tasks,
        CancellationToken ct = default)
    {
        if (tasks.Length is 0 or > SubAgentRunner.MaxParallel)
            throw new ToolException($"Give between 1 and {SubAgentRunner.MaxParallel} tasks; got {tasks.Length}.");
        return runner.RunParallelAsync(tasks, ct);
    }

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
