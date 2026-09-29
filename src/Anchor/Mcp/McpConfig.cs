using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anchor.Mcp;

/// <summary>One MCP server, in Claude Code's <c>mcpServers</c> shape: <c>command</c>/<c>args</c>/<c>env</c> for stdio, <c>url</c>/<c>headers</c> for HTTP.</summary>
public sealed class McpServerConfig
{
    public string? Type { get; set; }

    public string? Command { get; set; }

    public List<string> Args { get; set; } = [];

    public Dictionary<string, string> Env { get; set; } = [];

    public string? Cwd { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public McpOAuthConfig? OAuth { get; set; }

    public bool IsHttp => Type is "http" or "sse" or "streamable-http" || (Command is null && Url is not null);
}

/// <summary>Optional OAuth overrides; dynamic client registration covers the common case.</summary>
public sealed class McpOAuthConfig
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public List<string> Scopes { get; set; } = [];

    public int CallbackPort { get; set; } = 33418;
}

public sealed record McpServer(string Name, McpServerConfig Config, bool FromProject);

public static partial class McpConfig
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>User servers plus the project's <c>.mcp.json</c>; on a name clash the user's own config wins.</summary>
    public static (List<McpServer> Servers, List<string> Warnings) Load(Dictionary<string, McpServerConfig>? user, string workspaceRoot)
    {
        var servers = (user ?? []).Select(kv => new McpServer(kv.Key, kv.Value, false)).ToList();
        var warnings = new List<string>();
        var projectFile = Path.Combine(workspaceRoot, ".mcp.json");
        if (File.Exists(projectFile))
        {
            try
            {
                var project = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(projectFile), Json)?.McpServers ?? [];
                foreach (var (name, config) in project)
                {
                    if (servers.Any(s => s.Name == name))
                        warnings.Add($".mcp.json server '{name}' is ignored: your own config defines a server with that name.");
                    else
                        servers.Add(new McpServer(name, config, true));
                }
            }
            catch (JsonException e)
            {
                warnings.Add($".mcp.json is not valid JSON: {e.Message}");
            }
        }

        foreach (var s in servers.ToList())
        {
            var problem = s.Config.IsHttp
                ? s.Config.Url is null ? "an HTTP server needs a url" : null
                : s.Config.Command is null ? "a stdio server needs a command" : null;
            if (problem is null)
                continue;
            warnings.Add($"MCP server '{s.Name}' is skipped: {problem}.");
            servers.Remove(s);
        }
        return (servers, warnings);
    }

    /// <summary>Replaces <c>${NAME}</c> with the environment variable's value (empty when unset).</summary>
    public static string Expand(string value) =>
        EnvToken().Replace(value, m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

    /// <summary>Identifies a server's exact configuration, so a changed project config is asked about again.</summary>
    public static string Fingerprint(McpServerConfig config) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config))))[..16];

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvToken();

    sealed class ProjectFile
    {
        public Dictionary<string, McpServerConfig>? McpServers { get; set; }
    }
}

/// <summary>Remembers the user's answer about each project MCP server (by workspace, name and exact config).</summary>
public sealed class McpTrust(string path)
{
    public bool? Get(string workspace, McpServer server) =>
        Read().TryGetValue(Key(workspace, server), out var allowed) ? allowed : null;

    public void Set(string workspace, McpServer server, bool allowed)
    {
        var all = Read();
        all[Key(workspace, server)] = allowed;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Key(string workspace, McpServer server) => $"{workspace}|{server.Name}|{McpConfig.Fingerprint(server.Config)}";

    Dictionary<string, bool> Read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
