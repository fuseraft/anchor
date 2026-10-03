using Anchor.Cli;
using Anchor.Core;
using Anchor.Mcp;

var (options, exitCode) = Options.Parse(args, Console.Out, Console.Error);
if (options is null)
    return exitCode;

try
{
    if (options.Print)
    {
        var prompt = options.Prompt ?? "";
        if (options.Prompt is null ? Console.IsInputRedirected : StdinIsPipeOrFile())
            prompt = (prompt + "\n\n" + await Console.In.ReadToEndAsync()).Trim();
        if (prompt.Length == 0)
        {
            Console.Error.WriteLine("anchor: -p needs a prompt, as an argument or on stdin.");
            return 2;
        }

        var json = options.Json ? new JsonEvents(Console.Out) : null;
        var renderer = new Renderer(Console.Error, !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null, streamText: false);
        Action<string> warn = json is null ? m => renderer.Line(renderer.Yellow(m)) : m => json.Emit(new Notice(m));
        var output = new Output(json is null ? renderer.Render : json.Emit, new RefusingApprover(warn), warn, Interactive: false);
        var h = await Startup.BuildAsync(options, output);
        await using var _ = h.Mcp;
        return await PrintMode.RunAsync(h, prompt, json, Console.Out, options.Until, options.Timeout is { } t ? TimeSpan.FromSeconds(t) : null);
    }

    if (options.Json)
    {
        var json = new JsonEvents(Console.Out);
        var approver = new JsonApprover(json);
        var h = await Startup.BuildAsync(options, new Output(json.Emit, approver, m => json.Emit(new Notice(m)), Interactive: false));
        await using var _ = h.Mcp;
        return await new JsonMode(json, approver, Console.In).RunAsync(h);
    }

    if (options.Setup || !Console.IsInputRedirected && Setup.Needed(options, Config.Load()))
    {
        if (!options.Setup)
            Console.WriteLine("No model is configured yet, and no ANTHROPIC_API_KEY or XAI_API_KEY is set. Let's set one up.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var model = await new Setup(new ConsoleSetupIO(), Keychain.Default(), http, Path.Combine(Config.Home, "config.json")).RunAsync();
        if (options.Setup || model is null)
            return model is null ? 1 : 0;
        Console.WriteLine();
    }

    if (!options.Plain && Tui.Supported)
        return await Tui.RunAsync(options);

    {
        var renderer = Renderer.ForConsole();
        var h = await Startup.BuildAsync(options, new Output(renderer.Render, new ConsoleApprover(renderer), m => renderer.Line(renderer.Yellow(m)), Interactive: true));
        await using var _ = h.Mcp;
        var screen = new LineScreen(renderer, () => h.Session.Sync(h.Agent.History));
        return await new Repl(h.Agent, h.Gate, h.Session, renderer, screen, ReplOptions.From(h, options)).RunAsync();
    }
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine($"anchor: {e.Message}");
    return 1;
}

// With a prompt argument, piped input is extra context. A stdin that is a socket or a device (as under CI runners and
// other agents) may never reach end-of-file, so it is only read when it's a pipe or a file.
static bool StdinIsPipeOrFile()
{
    if (!Console.IsInputRedirected)
        return false;
    if (!OperatingSystem.IsLinux())
        return true;
    var target = new FileInfo("/proc/self/fd/0").LinkTarget ?? "";
    return target.StartsWith("pipe:", StringComparison.Ordinal) || (target.StartsWith('/') && !target.StartsWith("/dev/", StringComparison.Ordinal));
}
