using System.Text.Json;

namespace Anchor.Cli;

/// <summary>~/.anchor/config.json (ANCHOR_HOME overrides the directory).</summary>
public sealed class Config
{
    public int Version { get; set; } = 1;

    public ProviderConfig Provider { get; set; } = new();

    public Dictionary<string, Anchor.Providers.CustomProvider>? Providers { get; set; }

    public Dictionary<string, Anchor.Mcp.McpServerConfig>? McpServers { get; set; }

    public static string Home =>
        Environment.GetEnvironmentVariable("ANCHOR_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".anchor");

    public static Config Load()
    {
        var path = Path.Combine(Home, "config.json");
        if (!File.Exists(path))
            return new Config();
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions) ?? new Config();
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{path} is not valid JSON: {e.Message}");
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class ProviderConfig
{
    public string? Model { get; set; }

    public string? Name { get; set; }

    public string? Endpoint { get; set; }

    public string? ApiKeyEnv { get; set; }

    public long? ContextWindow { get; set; }
}
