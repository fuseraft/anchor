using Anchor.Cli;
using Anchor.Core;
using Anchor.Mcp;
using Anchor.Providers;
using Anchor.Tools;

string? model = null;
var yolo = false;
var resume = false;
string? resumeId = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model" or "-m" when i + 1 < args.Length:
            model = args[++i];
            break;
        case "--yolo":
            yolo = true;
            break;
        case "--resume" or "-r":
            resume = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                resumeId = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("""
                usage: anchor [--model <name>] [--yolo] [--resume [id]]

                Starts an interactive coding agent in the current directory.
                Writes, commands and reads outside the directory ask first; --yolo allows them
                without asking. Secret files and dangerous commands are always denied.
                --resume continues the latest session in this directory, or the given one.
                Config: ~/.anchor/config.json (ANCHOR_HOME overrides the directory).
                """);
            return 0;
        case "--version":
            Console.WriteLine(typeof(Agent).Assembly.GetName().Version?.ToString(3));
            return 0;
        default:
            Console.Error.WriteLine($"anchor: unknown argument '{args[i]}'. See anchor --help.");
            return 2;
    }
}

try
{
    var config = Config.Load();
    var provider = Providers.Resolve(model ?? config.Provider.Model, config.Provider.Name, config.Provider.Endpoint, config.Provider.ApiKeyEnv);
    var client = Providers.Create(provider);

    var workspace = new Workspace(Directory.GetCurrentDirectory());
    var renderer = Renderer.ForConsole();
    var usage = new SessionUsage();
    Action<AgentEvent> emit = e =>
    {
        usage.Observe(e);
        renderer.Render(e);
    };

    var (skills, skillWarnings) = Definitions.LoadSkills([Path.Combine(workspace.Root, ".agents", "skills"), Path.Combine(Config.Home, "skills")]);
    var (agents, agentWarnings) = Definitions.LoadAgents([Path.Combine(workspace.Root, ".agents", "agents"), Path.Combine(Config.Home, "agents")]);
    foreach (var warning in skillWarnings.Concat(agentWarnings))
        renderer.Line(renderer.Yellow(warning));

    var approver = new ConsoleApprover(renderer);
    var gate = new Gate(workspace, new Policy(workspace, yolo, skills.Select(s => s.Directory)), approver, emit);
    var toolbox = new Toolbox([.. new FileTools(gate).All(), .. new EditTools(gate).All(), .. new ShellTool(gate).All()]);
    var window = Providers.ContextWindow(provider, config.Provider.ContextWindow);
    var systemPrompt = SystemPrompt.Build(workspace, DateOnly.FromDateTime(DateTime.Now), skills);
    var agent = new Agent(client, toolbox, systemPrompt, emit, Providers.Options(provider), compactor: new Compactor(window));

    // Sub-agents use the main agent's current model unless their definition names one.
    var runner = new SubAgentRunner(toolbox, systemPrompt, emit, model =>
    {
        if (model is null)
            return (agent.Client, agent.Options);
        var settings = Providers.Resolve(model);
        return (Providers.Create(settings), Providers.Options(settings));
    }, () => new Compactor(Providers.ContextWindow(provider, config.Provider.ContextWindow)));
    toolbox.Add(new AgentTools(runner, agents, skills).All());

    var (servers, mcpWarnings) = McpConfig.Load(config.McpServers, workspace.Root);
    foreach (var warning in mcpWarnings)
        renderer.Line(renderer.Yellow(warning));
    var trust = new McpTrust(Path.Combine(Config.Home, "mcp-trust.json"));
    var trusted = new List<McpServer>();
    foreach (var server in servers)
    {
        // A cloned repo's .mcp.json can start any program, so the user decides once per server and config.
        bool? allowed = server.FromProject ? trust.Get(workspace.Root, server) : true;
        if (server.FromProject && allowed is null)
        {
            var what = server.Config.IsHttp ? server.Config.Url : string.Join(' ', [server.Config.Command!, .. server.Config.Args]);
            allowed = await approver.ApproveAsync(new ApprovalRequest($"This project's .mcp.json wants to start MCP server '{server.Name}': {what}", null, null), default) == Answer.Yes;
            trust.Set(workspace.Root, server, allowed.Value);
        }
        if (allowed != false)
            trusted.Add(server);
        else
            renderer.Line(renderer.Dim($"Not starting MCP server '{server.Name}' (declined earlier; to be asked again, remove it from {Path.Combine(Config.Home, "mcp-trust.json")})."));
    }
    await using var hub = new McpHub(toolbox, gate, emit, Keychain.Default());
    _ = hub.StartAsync(trusted, CancellationToken.None);

    var sessionsDir = Path.Combine(Config.Home, "sessions");
    var session = SessionLog.Create(sessionsDir, workspace.Root, provider.Model);
    if (resume)
    {
        (session, var history) = SessionLog.Open(sessionsDir, resumeId, workspace.Root, provider.Model);
        agent.History.AddRange(history);
    }

    return await new Repl(agent, gate, session, renderer, new ReplOptions(provider, sessionsDir, yolo, resume, window, skills, agents, usage, hub)).RunAsync();
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine($"anchor: {e.Message}");
    return 1;
}
