using System.Text.Json;
using Anchor.Core;

namespace Anchor.Cli;

/// <summary>~/.anchor/config.json (ANCHOR_HOME overrides the directory).</summary>
public sealed class Config
{
    public int Version { get; set; } = 1;

    public ProviderConfig Provider { get; set; } = new();

    public Dictionary<string, Anchor.Providers.CustomProvider>? Providers { get; set; }

    public Dictionary<string, Anchor.Mcp.McpServerConfig>? McpServers { get; set; }

    /// <summary>A built-in theme's name; see <see cref="Cli.Theme"/>.</summary>
    public string? Theme { get; set; }

    /// <summary>Colors for single roles, on top of the theme: "accent": "bold magenta".</summary>
    public Dictionary<string, string>? Colors { get; set; }

    /// <summary>Whether the full screen rings the terminal bell when a turn ends or a question waits; on unless false.</summary>
    public bool? Bell { get; set; }

    /// <summary>Whether anchor downloads new releases and installs them the next time it starts; on unless false.</summary>
    public bool? AutoUpdate { get; set; }

    public static string Home => Anchor.Core.AnchorHome.Dir;

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
            throw new AnchorException($"{path} is not valid JSON: {e.Message}");
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
