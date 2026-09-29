using System.Text;
using System.Text.Json;
using Anchor.Core;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Anchor.Mcp;

public sealed record McpStatus(string Name, string State, int Tools, string? Detail = null);

/// <summary>Connects the configured MCP servers in the background and offers their tools, gated, as mcp__server__tool.</summary>
public sealed class McpHub(Toolbox toolbox, Gate gate, Action<AgentEvent> emit, IKeychain keychain, Action<Uri>? openBrowser = null) : IAsyncDisposable
{
    readonly Dictionary<string, McpClient> _clients = [];
    readonly Dictionary<string, McpStatus> _status = [];
    readonly Dictionary<string, McpServer> _servers = [];
    readonly Lock _lock = new();

    public IReadOnlyList<McpStatus> Status
    {
        get { lock (_lock) return [.. _status.Values.OrderBy(s => s.Name, StringComparer.Ordinal)]; }
    }

    /// <summary>Connects one server after another, so two sign-ins never compete for the callback port.</summary>
    public Task StartAsync(IEnumerable<McpServer> servers, CancellationToken ct)
    {
        var list = servers.ToList();
        lock (_lock)
            foreach (var server in list)
            {
                _servers[server.Name] = server;
                _status[server.Name] = new McpStatus(server.Name, "connecting", 0);
            }
        return Task.Run(async () =>
        {
            foreach (var server in list)
                await ConnectAsync(server.Name, ct);
        }, ct);
    }

    public async Task ConnectAsync(string name, CancellationToken ct)
    {
        McpServer server;
        lock (_lock)
            server = _servers.TryGetValue(name, out var s) ? s : throw new InvalidOperationException($"No MCP server named '{name}'.");
        await DisconnectAsync(name);
        try
        {
            var client = await CreateClientAsync(server, ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            lock (_lock)
            {
                _clients[name] = client;
                _status[name] = new McpStatus(name, "connected", tools.Count);
            }
            toolbox.Add(tools.Select(t => new McpTool(name, t, client, gate)));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            lock (_lock)
                _status[name] = new McpStatus(name, "failed", 0, e.Message);
            emit(new Notice($"MCP server '{name}' is unavailable: {e.Message}"));
        }
    }

    /// <summary>Forgets a server's OAuth tokens and disconnects it; the config stays.</summary>
    public async Task LogoutAsync(string name)
    {
        McpServer server;
        lock (_lock)
            server = _servers.TryGetValue(name, out var s) ? s : throw new InvalidOperationException($"No MCP server named '{name}'.");
        if (server.Config.Url is { } url)
            await TokenStore.ForgetAsync(name, McpConfig.Expand(url), keychain);
        await DisconnectAsync(name);
        lock (_lock)
            _status[name] = new McpStatus(name, "signed out", 0);
    }

    async Task DisconnectAsync(string name)
    {
        McpClient? client;
        lock (_lock)
            _clients.Remove(name, out client);
        toolbox.Remove(t => t.StartsWith(McpTool.Prefix(name), StringComparison.Ordinal));
        if (client is not null)
            await client.DisposeAsync();
    }

    async Task<McpClient> CreateClientAsync(McpServer server, CancellationToken ct)
    {
        var config = server.Config;
        if (!config.IsHttp)
        {
            var stdio = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = McpConfig.Expand(config.Command!),
                Arguments = [.. config.Args.Select(McpConfig.Expand)],
                EnvironmentVariables = config.Env.ToDictionary(kv => kv.Key, kv => (string?)McpConfig.Expand(kv.Value)),
                WorkingDirectory = config.Cwd is null ? gate.Workspace.Root : Path.GetFullPath(McpConfig.Expand(config.Cwd), gate.Workspace.Root),
                // The SDK (2.1) waits this long before killing the server without closing its stdin first, so waiting longer only delays exit.
                ShutdownTimeout = TimeSpan.FromMilliseconds(500),
            });
            return await McpClient.CreateAsync(stdio, cancellationToken: ct);
        }

        var url = McpConfig.Expand(config.Url!);
        var oauth = config.OAuth ?? new McpOAuthConfig();
        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(url),
            TransportMode = config.Type == "sse" ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
            AdditionalHeaders = config.Headers.ToDictionary(kv => kv.Key, kv => McpConfig.Expand(kv.Value)),
            // Only used if the server answers 401: then the SDK registers a client, and the user signs in in the browser.
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri($"http://localhost:{oauth.CallbackPort}/callback"),
                ClientId = oauth.ClientId,
                ClientSecret = oauth.ClientSecret is { } secret ? McpConfig.Expand(secret) : null,
                Scopes = oauth.Scopes.Count > 0 ? oauth.Scopes : null,
                AuthorizationCallbackHandler = new BrowserLogin(server.Name, m => emit(new Notice(m)), openBrowser).AuthorizeAsync,
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "anchor" },
                TokenCache = new TokenStore(server.Name, url, keychain),
            },
        };
        var http = new HttpClient(new SameOriginRedirects(config.Headers.Keys));
        var transport = new HttpClientTransport(options, http, ownsHttpClient: true);
        // A person reading a consent page needs longer than the default 60 seconds.
        return await McpClient.CreateAsync(transport, new McpClientOptions { InitializationTimeout = TimeSpan.FromMinutes(5) }, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        List<McpClient> clients;
        lock (_lock)
        {
            clients = [.. _clients.Values];
            _clients.Clear();
        }
        await Task.WhenAll(clients.Select(async client =>
        {
            try { await client.DisposeAsync(); }
            catch (Exception) { }
        }));
    }
}

/// <summary>An MCP server's tool, exposed to the model under a namespaced name and called through the gate.</summary>
public sealed class McpTool(string server, McpClientTool tool, McpClient client, Gate gate) : AIFunction
{
    const int MaxNameLength = 64;

    public static string Prefix(string server) => $"mcp__{Clean(server)}__";

    public override string Name { get; } = ToolName(server, tool.Name);

    public static string ToolName(string server, string tool) => Truncate(Prefix(server) + Clean(tool));

    public override string Description => tool.Description;

    public override JsonElement JsonSchema => tool.JsonSchema;

    public bool ReadOnly => tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
    {
        var args = arguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        return await gate.CallExternalAsync(Name, ReadOnly, JsonSerializer.Serialize(args), async token =>
        {
            var result = await client.CallToolAsync(tool.Name, args, cancellationToken: token);
            var text = Format(result);
            return result.IsError == true ? throw new ToolException(text) : text;
        }, ct);
    }

    static string Format(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
            sb.Append(block is TextContentBlock t ? t.Text : $"[{block.Type} content omitted]").Append('\n');
        if (sb.Length == 0 && result.StructuredContent is { } structured)
            sb.Append(structured.ToString());
        return sb.ToString().TrimEnd();
    }

    // Provider function names allow only letters, digits, _ and -, up to 64 characters.
    static string Clean(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    static string Truncate(string name) =>
        name.Length <= MaxNameLength ? name : name[..(MaxNameLength - 9)] + "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
}
