using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace Anchor.Core;

/// <summary>One simple command after wrappers (env, nohup, xargs, ...) are peeled off.</summary>
public sealed record SimpleCommand(string Program, string RawProgram, IReadOnlyList<string> Args, IReadOnlyList<string> Writes,
    bool EnvPrefix, bool InSubstitution, int Pipeline, int Stage);

/// <summary>A deliberately conservative bash reader: classifies commands, never executes them.</summary>
public static partial class ShellCommand
{
    static readonly HashSet<string> Privileged = ["sudo", "sudoedit", "su", "doas", "pkexec", "run0"];
    static readonly HashSet<string> DiskTools = ["wipefs", "fdisk", "sfdisk", "parted", "mkswap"];
    static readonly HashSet<string> Fetchers = ["curl", "wget"];
    static readonly HashSet<string> Interpreters = ["sh", "bash", "zsh", "dash", "ksh", "fish", "python", "python3", "perl", "ruby", "node", "php"];
    static readonly HashSet<string> Shells = ["sh", "bash", "zsh", "dash", "ksh", "fish"];
    static readonly HashSet<string> Wrappers = ["command", "builtin", "exec", "nohup", "time", "stdbuf", "then", "do", "else", "elif", "if", "while", "until", "!", "{", "}"];
    static readonly HashSet<string> XargsValueOptions = ["-I", "-n", "-P", "-d", "-L", "-s", "-a", "-E"];
    static readonly string[] SecretSamples = [".env", ".env.local", ".env.production", "id_rsa", "id_ed25519", "id_ecdsa", ".netrc", ".pgpass", ".git-credentials"];

    static readonly HashSet<string> ReadOnlyPrograms =
    [
        "ls", "cat", "head", "tail", "wc", "grep", "egrep", "fgrep", "rg", "find", "pwd", "echo", "printf", "which", "file",
        "stat", "du", "df", "tree", "sort", "uniq", "cut", "diff", "cmp", "basename", "dirname", "realpath", "date",
        "whoami", "uname", "true", "tr", "nl", "column", "printenv", "git",
    ];

    static readonly HashSet<string> ReadOnlyGit = ["status", "diff", "log", "show", "blame", "ls-files", "rev-parse", "describe", "shortlog"];
    static readonly HashSet<string> FindWriters = ["-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls"];

    /// <summary>Why the command must never run, or null.</summary>
    public static string? Danger(string command)
    {
        if (Secrets.MentionedFile().IsMatch(command))
            return "it references a secret or credential file";

        var commands = Parse(command);
        foreach (var c in commands)
        {
            if (Privileged.Contains(c.Program))
                return "privilege escalation is not allowed";
            if (c.Program.StartsWith("mkfs", StringComparison.Ordinal) || DiskTools.Contains(c.Program)
                || (c.Program == "dd" && c.Args.Any(a => a.StartsWith("of=/dev/", StringComparison.Ordinal) && !IsHarmlessDevice(a[3..])))
                || c.Writes.Any(w => RawDisk().IsMatch(w)))
                return "raw disk operations are not allowed";
            if (c.Program == "rm" && IsCatastrophicDelete(c.Args))
                return "it would delete a system or home directory";
            if (c.Args.Any(MatchesSecretGlob))
                return "a glob in it matches secret or credential files";
        }

        foreach (var fetch in commands.Where(c => Fetchers.Contains(c.Program)))
        {
            var piped = commands.Any(c => Interpreters.Contains(c.Program) && c.Pipeline == fetch.Pipeline && c.Stage > fetch.Stage);
            var substituted = fetch.InSubstitution && commands.Any(c => Interpreters.Contains(c.Program));
            if (piped || substituted)
                return "downloading and executing code is not allowed";
        }
        return null;
    }

    /// <summary>True when every command only reads, and only inside the workspace (<paramref name="isInside"/>).</summary>
    public static bool IsReadOnly(string command, Func<string, bool> isInside)
    {
        if (command.Contains('$') || command.Contains('`'))
            return false;
        var commands = Parse(command);
        return commands.Count > 0 && commands.All(c => IsReadOnly(c, isInside));
    }

    /// <summary>Program names in the command, used for "always allow" approvals.</summary>
    public static IReadOnlySet<string> Programs(string command) => Parse(command).Select(c => c.Program).ToHashSet();

    static bool IsReadOnly(SimpleCommand c, Func<string, bool> isInside)
    {
        if (c.EnvPrefix || c.InSubstitution || c.RawProgram.Contains('/') || c.Writes.Any(w => w != "/dev/null"))
            return false;
        if (!ReadOnlyPrograms.Contains(c.Program))
            return false;
        if (c.Program == "find" && c.Args.Any(FindWriters.Contains))
            return false;
        if (c.Program == "sort" && c.Args.Any(a => a == "-o" || a.StartsWith("--output", StringComparison.Ordinal)))
            return false;
        if (c.Program == "git" && !IsReadOnlyGit(c.Args))
            return false;

        foreach (var arg in c.Args)
        {
            var value = arg.StartsWith('-') && arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : arg;
            if (value.StartsWith('~') || value.Split('/').Contains(".."))
                return false;
            if (value.StartsWith('/') && value != "/dev/null" && !isInside(value))
                return false;
        }
        return true;
    }

    static bool IsReadOnlyGit(IReadOnlyList<string> args) =>
        args.Count > 0 && ReadOnlyGit.Contains(args[0])
        && !args.Any(a => a.StartsWith("--output", StringComparison.Ordinal) || a.StartsWith("--ext-diff", StringComparison.Ordinal));

    static bool IsHarmlessDevice(string path) => path is "/dev/null" or "/dev/stdout" or "/dev/stderr" or "/dev/zero";

    static bool IsCatastrophicDelete(IReadOnlyList<string> args)
    {
        var recursive = args.Any(a => a is "-r" or "-R" or "--recursive" || (a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && (a.Contains('r') || a.Contains('R'))));
        if (args.Contains("--no-preserve-root"))
            return true;
        return recursive && args.Where(a => !a.StartsWith('-')).Any(a => CriticalTarget().IsMatch(a));
    }

    static bool MatchesSecretGlob(string arg)
    {
        if (arg.IndexOfAny(['*', '?', '[']) < 0)
            return false;
        var name = arg[(arg.LastIndexOf('/') + 1)..];
        return SecretSamples.Any(s => FileSystemName.MatchesSimpleExpression(name, s, ignoreCase: false));
    }

    [GeneratedRegex(@"^(/|/\*|~|~/|~/\*|\$HOME|\$\{HOME\}|\$HOME/\*?|\$\{HOME\}/\*?|/[^/]+/?\*?)$")]
    private static partial Regex CriticalTarget();

    [GeneratedRegex(@"^/dev/(sd|nvme|hd|vd|xvd|disk|mmcblk)")]
    private static partial Regex RawDisk();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    public static List<SimpleCommand> Parse(string command)
    {
        var parser = new Parser();
        parser.Parse(command, inSubstitution: false);
        return parser.Commands;
    }

    sealed class Parser
    {
        int _pipeline;

        public List<SimpleCommand> Commands { get; } = [];

        public void Parse(string text, bool inSubstitution)
        {
            var bodies = new List<string>();
            text = ExtractSubstitutions(text, bodies);
            foreach (var body in bodies)
                Parse(body, inSubstitution: true);

            var words = new List<string>();
            var writes = new List<string>();
            var sb = new StringBuilder();
            bool inWord = false, pendingWrite = false;
            int stage = 0;

            void EndWord()
            {
                if (!inWord)
                    return;
                (pendingWrite ? writes : words).Add(sb.ToString());
                sb.Clear();
                inWord = pendingWrite = false;
            }

            void EndCommand(bool pipe)
            {
                EndWord();
                if (words.Count > 0)
                    Expand(words, writes, inSubstitution, _pipeline, stage);
                words = [];
                writes = [];
                if (pipe)
                    stage++;
                else
                {
                    _pipeline++;
                    stage = 0;
                }
            }

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                switch (c)
                {
                    case '\\' when next == '\n':
                        i++;
                        break;
                    case '\\':
                        if (next != '\0')
                            sb.Append(text[++i]);
                        inWord = true;
                        break;
                    case '\'':
                        var close = text.IndexOf('\'', i + 1);
                        close = close < 0 ? text.Length : close;
                        sb.Append(text, i + 1, close - i - 1);
                        inWord = true;
                        i = close;
                        break;
                    case '"':
                        for (i++; i < text.Length && text[i] != '"'; i++)
                        {
                            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                                i++;
                            sb.Append(text[i]);
                        }
                        inWord = true;
                        break;
                    case '#' when !inWord:
                        while (i + 1 < text.Length && text[i + 1] != '\n')
                            i++;
                        break;
                    case ' ' or '\t':
                        EndWord();
                        break;
                    case '\n' or ';' or '(' or ')':
                        EndCommand(pipe: false);
                        break;
                    case '&' when next == '>':
                        EndWord();
                        i++;
                        if (i + 1 < text.Length && text[i + 1] == '>')
                            i++;
                        pendingWrite = true;
                        break;
                    case '&':
                        if (next == '&')
                            i++;
                        EndCommand(pipe: false);
                        break;
                    case '|':
                        if (next == '|')
                        {
                            i++;
                            EndCommand(pipe: false);
                        }
                        else
                        {
                            if (next == '&')
                                i++;
                            EndCommand(pipe: true);
                        }
                        break;
                    case '>' or '<':
                        if (inWord && sb.ToString().All(char.IsAsciiDigit))
                        {
                            sb.Clear();
                            inWord = false;
                        }
                        else
                            EndWord();
                        while (i + 1 < text.Length && text[i + 1] is '>' or '<' or '|')
                            i++;
                        if (i + 1 < text.Length && text[i + 1] == '&')
                        {
                            i++;
                            while (i + 1 < text.Length && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] == '-'))
                                i++;
                        }
                        else if (c == '>')
                            pendingWrite = true;
                        break;
                    default:
                        sb.Append(c);
                        inWord = true;
                        break;
                }
            }
            EndCommand(pipe: false);
        }

        void Expand(List<string> words, List<string> writes, bool inSubstitution, int pipeline, int stage)
        {
            var i = 0;
            var envPrefix = false;
            while (i < words.Count && Assignment().IsMatch(words[i]))
            {
                envPrefix = true;
                i++;
            }

            while (i < words.Count)
            {
                var name = Path.GetFileName(words[i]);
                if (name == "env")
                {
                    for (i++; i < words.Count && (words[i].StartsWith('-') || Assignment().IsMatch(words[i])); i++)
                        if (words[i] is "-u" or "-C" or "-S")
                            i++;
                    envPrefix = true;
                }
                else if (name == "nice" || name == "timeout" || name == "xargs" || Wrappers.Contains(name))
                {
                    for (i++; i < words.Count && words[i].StartsWith('-'); i++)
                        if ((name == "xargs" && XargsValueOptions.Contains(words[i])) || (name == "nice" && words[i] == "-n"))
                            i++;
                    if (name == "timeout" && i < words.Count)
                        i++;
                }
                else
                    break;
            }
            if (i >= words.Count)
                return;

            var program = Path.GetFileName(words[i]);
            var args = words.Skip(i + 1).ToList();
            Commands.Add(new SimpleCommand(program, words[i], args, writes, envPrefix, inSubstitution, pipeline, stage));

            if (Shells.Contains(program))
            {
                var flag = args.FindIndex(a => a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('c'));
                if (flag >= 0 && flag + 1 < args.Count)
                    Parse(args[flag + 1], inSubstitution);
            }
            else if (program == "eval")
                Parse(string.Join(' ', args), inSubstitution);
            else if (program == "find")
            {
                for (var k = 0; k < args.Count; k++)
                {
                    if (args[k] is not ("-exec" or "-execdir" or "-ok" or "-okdir"))
                        continue;
                    var end = args.FindIndex(k + 1, a => a is ";" or "+");
                    var sub = args.Skip(k + 1).Take((end < 0 ? args.Count : end) - k - 1).ToList();
                    if (sub.Count > 0)
                        Expand(sub, [], inSubstitution, pipeline, stage);
                }
            }
        }

        // Pulls out $(...), <(...), >(...) and `...` bodies (outside single quotes) so each is parsed as its own command.
        static string ExtractSubstitutions(string text, List<string> bodies)
        {
            var sb = new StringBuilder();
            var inDouble = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(text, i, 2);
                    i++;
                }
                else if (c == '"')
                {
                    inDouble = !inDouble;
                    sb.Append(c);
                }
                else if (c == '\'' && !inDouble)
                {
                    var close = text.IndexOf('\'', i + 1);
                    close = close < 0 ? text.Length - 1 : close;
                    sb.Append(text, i, close - i + 1);
                    i = close;
                }
                else if (c is '$' or '<' or '>' && i + 1 < text.Length && text[i + 1] == '(')
                {
                    var depth = 0;
                    var start = i + 2;
                    var j = start - 1;
                    for (; j < text.Length; j++)
                    {
                        if (text[j] == '(')
                            depth++;
                        else if (text[j] == ')' && --depth == 0)
                            break;
                    }
                    bodies.Add(text[start..Math.Min(j, text.Length)]);
                    sb.Append(" __sub__ ");
                    i = j;
                }
                else if (c == '`')
                {
                    var close = text.IndexOf('`', i + 1);
                    close = close < 0 ? text.Length : close;
                    bodies.Add(text[(i + 1)..close]);
                    sb.Append(" __sub__ ");
                    i = close;
                }
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
