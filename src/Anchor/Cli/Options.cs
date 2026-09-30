namespace Anchor.Cli;

/// <summary>Command-line options. <see cref="Parse"/> returns an exit code instead when it handled the request itself.</summary>
public sealed record Options(string? Model, bool Yolo, bool Resume, string? ResumeId, bool Print, string? Prompt, bool Json, string? Until = null,
    IReadOnlyList<string>? Allow = null, int? MaxRounds = null, int? Timeout = null, bool Setup = false)
{
    public const string Usage = """
        usage: anchor setup
               anchor [--model <name>] [--yolo] [--allow <rule>]... [--resume [id]] [-p [prompt]] [--until <check>]
                      [--max-rounds <n>] [--timeout <seconds>] [--json]

        Starts an interactive coding agent in the current directory.
        anchor setup chooses a provider, saves its API key and picks a model.

          -m, --model <name>   model to use (default: provider.model in the config)
          --yolo               don't ask before writes, commands and reads outside the directory
                               (secret files and dangerous commands are always denied)
          --allow <rule>       don't ask for this, for this run only; repeatable. <rule> is a program
                               (dotnet), an MCP tool (mcp__server__tool), or edits (file writes in the directory)
          -r, --resume [id]    continue the latest session in this directory, or the given one
          -p, --print [prompt] run one prompt and print the answer; piped stdin is appended to it
                               (without --yolo, anything that would ask is refused)
          --until <check>      with -p: after each turn run <check>, and keep working until it exits 0
                               (at most 5 rounds; exit code 4 if it never passes)
          --max-rounds <n>     with -p: stop after n model requests (exit code 5)
          --timeout <seconds>  with -p: stop after this long (exit code 124)
          --json               write events as JSON lines; without -p, read requests from stdin
          --version, --help

        Config: ~/.anchor/config.json (ANCHOR_HOME overrides the directory).
        """;

    public static (Options? Options, int ExitCode) Parse(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["setup"])
            return (new Options(null, false, false, null, false, null, false, Setup: true), 0);

        string? model = null, resumeId = null, prompt = null, until = null;
        bool yolo = false, resume = false, print = false, json = false;
        List<string> allow = [];
        int? maxRounds = null, timeout = null;
        for (var i = 0; i < args.Length; i++)
        {
            bool HasValue() => i + 1 < args.Length && !args[i + 1].StartsWith('-');
            switch (args[i])
            {
                case "--model" or "-m" when HasValue():
                    model = args[++i];
                    break;
                case "--yolo":
                    yolo = true;
                    break;
                case "--resume" or "-r":
                    resume = true;
                    if (HasValue())
                        resumeId = args[++i];
                    break;
                case "--print" or "-p":
                    print = true;
                    if (HasValue())
                        prompt = args[++i];
                    break;
                case "--until" when i + 1 < args.Length:
                    until = args[++i];
                    break;
                case "--allow" when i + 1 < args.Length:
                    allow.Add(args[++i]);
                    break;
                case "--max-rounds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var rounds) && rounds > 0:
                    maxRounds = rounds;
                    i++;
                    break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds) && seconds > 0:
                    timeout = seconds;
                    i++;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return (null, 0);
                case "--version":
                    output.WriteLine(Version);
                    return (null, 0);
                case var positional when !positional.StartsWith('-') && prompt is null:
                    prompt = positional;
                    break;
                default:
                    error.WriteLine($"anchor: unknown argument '{args[i]}'. See anchor --help.");
                    return (null, 2);
            }
        }
        if (prompt is not null && !print)
        {
            error.WriteLine("anchor: a prompt argument needs -p (to run it and exit).");
            return (null, 2);
        }
        if (until is not null && !print)
        {
            error.WriteLine("anchor: --until needs -p; in the REPL, use /until <check>.");
            return (null, 2);
        }
        if ((maxRounds ?? timeout) is not null && !print)
        {
            error.WriteLine("anchor: --max-rounds and --timeout need -p.");
            return (null, 2);
        }
        return (new Options(model, yolo, resume, resumeId, print, prompt, json, until, allow.Count > 0 ? allow : null, maxRounds, timeout), 0);
    }

    public static string Version
    {
        get
        {
            var version = typeof(Options).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is [System.Reflection.AssemblyInformationalVersionAttribute v, ..] ? v.InformationalVersion : "0.0.0";
            var plus = version.IndexOf('+');
            return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
        }
    }
}
