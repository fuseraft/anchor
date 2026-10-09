using System.Diagnostics;

namespace Anchor.Cli;

/// <summary>What the REPL needs from a front end: the full-screen TUI, or plain lines when there's no terminal.</summary>
public interface IReplScreen
{
    /// <summary>True when input is separate from the transcript, so the REPL keeps reading while a turn runs.</summary>
    bool FullScreen { get; }

    /// <summary>Called on Ctrl+C; returns true when it cancelled something (a turn, a shell command).</summary>
    Func<bool>? Interrupt { set; }

    /// <summary>The next line the user sends; null ends the session.</summary>
    Task<string?> ReadAsync(CancellationToken ct);

    /// <summary>Shows a line the user sent in the transcript; a plain terminal already shows what was typed.</summary>
    void Echo(string text);

    /// <summary>Empties the transcript, for /clear. A plain terminal's scrollback is the terminal's, so it stays.</summary>
    void Clear() { }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard, for /copy: through the terminal (OSC 52) and the system's
    /// clipboard tool. Returns the tool's name, or null when only the terminal was asked.
    /// </summary>
    string? Copy(string text)
    {
        if (!Console.IsOutputRedirected)
        {
            Console.Out.Write(Clipboard.Osc52(text));
            Console.Out.Flush();
        }
        return Clipboard.CopyNatively(text);
    }

    /// <summary>Shows where things stand: the model, how full the context is, and whether a turn is running.</summary>
    void Status(string text, bool working);

    /// <summary>Runs the user's own shell command (<c>!cmd</c>) and returns its exit code.</summary>
    Task<int> ShellAsync(string command, string directory, CancellationToken ct);

    ISetupIO SetupIO { get; }
}

/// <summary>The REPL on a plain terminal or piped input: Console.ReadLine, and Ctrl+C through CancelKeyPress.</summary>
public sealed class LineScreen : IReplScreen
{
    // A terminal delivers a paste in one burst, so input still waiting this long after Enter is part of the same paste.
    static readonly TimeSpan PasteGap = TimeSpan.FromMilliseconds(50);

    readonly Renderer _renderer;
    readonly Action _exiting;
    DateTime _lastIdleInterrupt;

    /// <param name="exiting">Runs before a double Ctrl+C ends the process, to save the session.</param>
    public LineScreen(Renderer renderer, Action exiting)
    {
        _renderer = renderer;
        _exiting = exiting;
        Console.CancelKeyPress += OnCancel;
    }

    public bool FullScreen => false;

    public Func<bool>? Interrupt { private get; set; }

    public ISetupIO SetupIO { get; } = new ConsoleSetupIO();

    public void Status(string text, bool working)
    {
    }

    public void Echo(string text)
    {
    }

    // Reads one line, plus the rest of a multi-line paste, so a paste becomes one message instead of one turn per line.
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        _renderer.Write("\n" + _renderer.Prompt);
        _renderer.AtPrompt = true;
        try
        {
            var line = Console.ReadLine();
            if (line is null || Console.IsInputRedirected)
                return line;

            List<string> lines = [line];
            while (true)
            {
                await Task.Delay(PasteGap, ct);
                if (!Console.KeyAvailable || Console.ReadLine() is not { } next)
                    break;
                lines.Add(next);
            }
            return string.Join('\n', lines);
        }
        finally
        {
            _renderer.AtPrompt = false;
        }
    }

    public async Task<int> ShellAsync(string command, string directory, CancellationToken ct)
    {
        using var process = Process.Start(Shell(command, directory))!;
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    public static ProcessStartInfo Shell(string command, string directory)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", command])
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh", ["-c", command]);
        psi.WorkingDirectory = directory;
        return psi;
    }

    void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (Interrupt?.Invoke() == true)
            return;
        if (DateTime.UtcNow - _lastIdleInterrupt < TimeSpan.FromSeconds(2))
        {
            _exiting();
            Environment.Exit(0);
        }
        _lastIdleInterrupt = DateTime.UtcNow;
        _renderer.Write("\n(press Ctrl+C again or Ctrl+D to exit)\n" + _renderer.Prompt);
    }
}
