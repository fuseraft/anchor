using System.Diagnostics;
using Anchor.Core;
using Anchor.Providers;

namespace Anchor.Cli;

/// <summary>The interactive loop: read a line, run it as a slash command, a shell escape, or an agent turn.</summary>
public sealed class Repl(Agent agent, Renderer renderer, Workspace workspace, ProviderSettings provider, bool yolo = false)
{
    CancellationTokenSource? _turn;
    DateTime _lastIdleInterrupt;
    ProviderSettings _provider = provider;

    public async Task<int> RunAsync()
    {
        Console.CancelKeyPress += OnCancel;
        renderer.Line($"{renderer.Bold("anchor")} {renderer.Dim($"· {_provider.Model} · {workspace.Root}")}");
        if (yolo)
            renderer.Line(renderer.Yellow("--yolo: writes, commands and outside reads run without asking. Secret files and dangerous commands are still denied."));
        renderer.Line(renderer.Dim("/help for commands, Ctrl+D to exit"));

        while (true)
        {
            Console.Write("\n› ");
            var line = Console.ReadLine();
            if (line is null)
                return 0;
            line = line.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('!'))
                await ShellAsync(line[1..]);
            else if (line.StartsWith('/'))
            {
                if (!Command(line))
                    return 0;
            }
            else
                await TurnAsync(line);
        }
    }

    async Task TurnAsync(string input)
    {
        using var cts = new CancellationTokenSource();
        _turn = cts;
        try
        {
            Console.WriteLine();
            await agent.RunTurnAsync(input, cts.Token);
        }
        finally
        {
            _turn = null;
        }
    }

    bool Command(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (parts[0])
        {
            case "/exit" or "/quit":
                return false;
            case "/clear":
                agent.History.Clear();
                renderer.Line(renderer.Dim("History cleared."));
                break;
            case "/model" when parts.Length == 1:
                renderer.Line($"{_provider.Model} {renderer.Dim($"({_provider.Provider})")}");
                break;
            case "/model":
                try
                {
                    var next = Providers.Providers.Resolve(parts[1]);
                    agent.Use(Providers.Providers.Create(next), Providers.Providers.Options(next));
                    _provider = next;
                    renderer.Line(renderer.Dim($"Model: {next.Model}"));
                }
                catch (InvalidOperationException e)
                {
                    renderer.Line(renderer.Red(e.Message));
                }
                break;
            case "/help":
                renderer.Line("""
                    /model [name]   show or switch the model
                    /clear          forget the conversation
                    /exit           quit (or Ctrl+D)
                    !<command>      run a shell command yourself; the model never sees it
                    Ctrl+C          cancel the running turn
                    """);
                break;
            default:
                renderer.Line(renderer.Red($"Unknown command {parts[0]}. Try /help."));
                break;
        }
        return true;
    }

    async Task ShellAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", command])
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh", ["-c", command]);
        psi.WorkingDirectory = workspace.Root;
        try
        {
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                renderer.Line(renderer.Dim($"exit {process.ExitCode}"));
        }
        catch (Exception e)
        {
            renderer.Line(renderer.Red(e.Message));
        }
    }

    void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (_turn is { } turn)
        {
            turn.Cancel();
            return;
        }
        if (DateTime.UtcNow - _lastIdleInterrupt < TimeSpan.FromSeconds(2))
            Environment.Exit(0);
        _lastIdleInterrupt = DateTime.UtcNow;
        Console.Write("\n(press Ctrl+C again or Ctrl+D to exit)\n› ");
    }
}
