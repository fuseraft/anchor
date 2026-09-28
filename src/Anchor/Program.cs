using Anchor.Cli;
using Anchor.Core;
using Anchor.Providers;
using Anchor.Tools;

string? model = null;
var yolo = false;
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
        case "--help" or "-h":
            Console.WriteLine("""
                usage: anchor [--model <name>] [--yolo]

                Starts an interactive coding agent in the current directory.
                Writes, commands and reads outside the directory ask first; --yolo allows them
                without asking. Secret files and dangerous commands are always denied.
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
    var gate = new Gate(workspace, new Policy(workspace, yolo), new ConsoleApprover(renderer), renderer.Render);
    var toolbox = new Toolbox([.. new FileTools(gate).All(), .. new EditTools(gate).All(), .. new ShellTool(gate).All()]);
    var agent = new Agent(client, toolbox, SystemPrompt.Build(workspace, DateOnly.FromDateTime(DateTime.Now)), renderer.Render, Providers.Options(provider));

    return await new Repl(agent, renderer, workspace, provider, yolo).RunAsync();
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine($"anchor: {e.Message}");
    return 1;
}
