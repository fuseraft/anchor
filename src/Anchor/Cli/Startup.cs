using Anchor.Core;
using Anchor.Mcp;
using Anchor.Providers;
using Anchor.Tools;

namespace Anchor.Cli;

/// <summary>Everything a mode needs, wired the same way for the REPL, -p and --json.</summary>
public sealed record Harness(
    Agent Agent, Gate Gate, SessionLog Session, McpHub Mcp, Task McpReady, ProviderSettings Provider, long ContextWindow,
    string SessionsDir, IReadOnlyList<Skill> Skills, IReadOnlyList<AgentDefinition> Agents, SessionUsage Usage,
    ModelSource Models, SubAgentRunner? SubAgents = null);

/// <summary>How a mode shows events, asks for approval, and reports warnings.</summary>
public sealed record Output(Action<AgentEvent> Emit, IApprover Approver, Action<string> Warn, bool Interactive);

public static class Startup
{
    public static async Task<Harness> BuildAsync(Options options, Output output)
    {
        var config = Config.Load();
        var keychain = Keychain.Default();
        var usage = new SessionUsage();
        Action<AgentEvent> emit = e =>
        {
            usage.Observe(e);
            output.Emit(e);
        };
        var models = new ModelSource(ModelSource.StoredKeys(keychain, new CredentialsFile(AnchorHome.Credentials)), Config.Load, m => emit(new Notice(m)));
        var provider = Providers.Providers.Resolve(options.Model ?? config.Provider.Model, config.Provider.Name, config.Provider.Endpoint, config.Provider.ApiKeyEnv, config.Providers);
        var client = await models.CreateAsync(provider);
        var window = Providers.Providers.ContextWindow(provider, config.Provider.ContextWindow);

        var workspace = new Workspace(Directory.GetCurrentDirectory());

        var (skills, skillWarnings) = Definitions.LoadSkills([Path.Combine(workspace.Root, ".agents", "skills"), Path.Combine(Config.Home, "skills")]);
        var (agents, agentWarnings) = Definitions.LoadAgents([Path.Combine(workspace.Root, ".agents", "agents"), Path.Combine(Config.Home, "agents")]);
        var themeWarnings = Theme.From(options.Theme ?? config.Theme, config.Colors).Warnings;
        foreach (var warning in themeWarnings.Concat(skillWarnings).Concat(agentWarnings))
            output.Warn(warning);

        // Background sub-agents can need the user at the same time as the main agent; they take turns.
        var approver = new SerialApprover(output.Approver);
        var gate = new Gate(workspace, new Policy(workspace, options.Yolo, skills.Select(s => s.Directory)), approver, emit,
            new ApprovalStore(Path.Combine(Config.Home, "approvals.json"), workspace.Root));
        foreach (var rule in options.Allow ?? [])
            gate.Allow(rule);
        var toolbox = new Toolbox([.. new FileTools(gate).All(), .. new EditTools(gate).All(), .. new PatchTool(gate).All(), .. new ShellTool(gate).All()]);
        var systemPrompt = SystemPrompt.Build(workspace, DateOnly.FromDateTime(DateTime.Now), skills);
        var agent = new Agent(client, toolbox, systemPrompt, emit, Providers.Providers.Options(provider), compactor: new Compactor(window))
        {
            MaxRounds = options.MaxRounds,
        };

        // Sub-agents use the main agent's current model unless their definition names one.
        var runner = new SubAgentRunner(toolbox, systemPrompt, emit, async model =>
        {
            if (model is null)
                return (agent.Client, agent.Options);
            var settings = models.Resolve(model);
            return (await models.CreateAsync(settings), Providers.Providers.Options(settings));
        }, () => new Compactor(window), options.MaxRounds)
        {
            Report = agent.Notify,
        };
        agent.Background = runner;
        toolbox.Add(new AgentTools(runner, agents, skills).All());
        if (approver.CanAsk)
            toolbox.Add(new AskTool(approver).All());

        var hub = new McpHub(toolbox, gate, emit, keychain,
            connectTimeout: options.Timeout is { } t ? TimeSpan.FromSeconds(t) : null);
        var ready = hub.StartAsync(await TrustedServersAsync(config, workspace, output), CancellationToken.None);

        var sessionsDir = Path.Combine(Config.Home, "sessions");
        var session = SessionLog.Create(sessionsDir, workspace.Root, provider.Model);
        if (options.Resume)
        {
            (session, var history) = SessionLog.Open(sessionsDir, options.ResumeId, workspace.Root, provider.Model);
            agent.Restore(history);
        }
        return new Harness(agent, gate, session, hub, ready, provider, window, sessionsDir, skills, agents, usage, models, runner);
    }

    // A cloned repo's .mcp.json can start any program, so the user decides once per server and config.
    // Without a person to ask (-p, --json -p), unknown project servers are skipped and nothing is remembered.
    static async Task<List<McpServer>> TrustedServersAsync(Config config, Workspace workspace, Output output)
    {
        var (servers, warnings) = McpConfig.Load(config.McpServers, workspace.Root);
        foreach (var warning in warnings)
            output.Warn(warning);

        var trustFile = Path.Combine(Config.Home, "mcp-trust.json");
        var trust = new McpTrust(trustFile);
        var trusted = new List<McpServer>();
        foreach (var server in servers)
        {
            var allowed = server.FromProject ? trust.Get(workspace.Root, server) : true;
            if (allowed is null && output.Interactive)
            {
                var what = server.Config.IsHttp ? server.Config.Url : string.Join(' ', [server.Config.Command!, .. server.Config.Args]);
                allowed = await output.Approver.ApproveAsync(
                    new ApprovalRequest($"This project's .mcp.json wants to start MCP server '{server.Name}': {what}", null, null), default) == Answer.Yes;
                trust.Set(workspace.Root, server, allowed.Value);
            }

            if (allowed == true)
                trusted.Add(server);
            else if (allowed is null)
                output.Warn($"Not starting project MCP server '{server.Name}': run anchor interactively once to approve it.");
            else
                output.Warn($"Not starting MCP server '{server.Name}' (declined earlier; to be asked again, remove it from {trustFile}).");
        }
        return trusted;
    }
}
