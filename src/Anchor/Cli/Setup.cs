using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anchor.Mcp;
using Anchor.Providers;

namespace Anchor.Cli;

/// <summary>How the setup wizard talks to the person; null from an ask means input ended.</summary>
public interface ISetupIO
{
    string? Ask(string prompt);

    string? AskSecret(string prompt);

    /// <summary>One of <paramref name="choices"/>; with <paramref name="allowTyped"/>, also a name typed that matches none.</summary>
    string? Select(string title, IReadOnlyList<string> choices, string? selected = null, bool allowTyped = false);

    void Line(string text = "");
}

/// <summary><c>anchor setup</c>: choose a provider, save its key, pick a model, and write config.json.</summary>
public sealed class Setup(ISetupIO io, IKeychain keychain, HttpClient http, string configPath)
{
    const string AnotherServer = "Another server: LiteLLM, Ollama, vLLM or anything OpenAI-compatible";

    sealed record Service(string Label, string Type, string Endpoint, string ApiKeyEnv, string[] Prefixes, string? DefaultModel);

    static readonly Service[] Services =
    [
        new("Anthropic", "anthropic", "https://api.anthropic.com", "ANTHROPIC_API_KEY", ["claude-"], "claude-sonnet-5"),
        new("OpenAI", "openai", "https://api.openai.com/v1", "OPENAI_API_KEY", ["gpt-", "o1", "o3", "o4"], null),
        new("xAI", "openai", "https://api.x.ai/v1", "XAI_API_KEY", ["grok-"], "grok-4.5"),
    ];

    /// <summary>True when nothing names a model, so a first interactive run should offer setup.</summary>
    public static bool Needed(Options options, Config config) =>
        options.Model is null && config.Provider.Model is null && !Providers.Providers.HasDefaultModel();

    /// <summary>Runs the wizard and saves the result. Returns the model to use, or null if the person stopped.</summary>
    public async Task<string?> RunAsync(CancellationToken ct = default)
    {
        io.Line($"Sets up a provider and model in {configPath}.");
        string[] sources = [.. Services.Select(s => s.Label), AnotherServer];
        var choice = io.Select("Where do your models come from?", sources);
        if (choice is null)
            return null;
        var service = Services.FirstOrDefault(s => s.Label == choice);
        var model = service is not null ? await BuiltInAsync(service, ct) : await CustomAsync(ct);
        if (model is null)
            return null;

        io.Line($"Saved {configPath}. Model: {model}");
        return model;
    }

    async Task<string?> BuiltInAsync(Service service, CancellationToken ct)
    {
        var (ok, key) = await KeyAsync(service.ApiKeyEnv, required: true);
        if (!ok)
            return null;

        var models = await ListAsync(service.Type, service.Endpoint, key, null, ct);
        models = models?.Where(m => service.Prefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
        var model = PickModel(models, service.DefaultModel);
        if (model is null)
            return null;

        Save(root =>
        {
            var provider = ProviderObject(root);
            provider.Clear();
            provider["model"] = model;
        });
        return model;
    }

    async Task<string?> CustomAsync(CancellationToken ct)
    {
        var url = Ask("Server URL, such as https://litellm.example.com/v1");
        if (url is null)
            return null;
        url = url.TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            io.Line($"'{url}' is not an http or https URL.");
            return null;
        }

        var existing = Load()["providers"]?.AsObject();
        var name = Ask("Name for this server; you'll pick its models as <name>/<model>", DefaultName(uri));
        if (name is null)
            return null;
        if (name.Contains('/') || name.Any(char.IsWhiteSpace))
        {
            io.Line("The name can't contain '/' or spaces.");
            return null;
        }

        var entry = existing?[name]?.AsObject();
        var type = entry?["type"]?.GetValue<string>() ?? "openai";
        var headers = entry?["headers"]?.Deserialize<Dictionary<string, string>>();
        var envDefault = entry?["apiKeyEnv"]?.GetValue<string>() ?? EnvName(name);
        var env = Ask("Environment variable for its API key (none if it needs no key)", envDefault);
        if (env is null)
            return null;
        env = env.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : env;

        string? key = null;
        if (env is not null)
        {
            (var ok, key) = await KeyAsync(env, required: false);
            if (!ok)
                return null;
        }

        var models = await ListAsync(type, url, key, headers, ct, quiet: true);
        if (models is null && type == "openai" && !url.EndsWith("/v1", StringComparison.Ordinal))
        {
            models = await ListAsync(type, url + "/v1", key, headers, ct, quiet: true);
            if (models is not null)
                url += "/v1";
        }
        if (models is null)
            await ListAsync(type, url, key, headers, ct);

        var model = PickModel(models, null);
        if (model is null)
            return null;

        var reference = $"{name}/{model}";
        Save(root =>
        {
            var providers = root["providers"] as JsonObject ?? (JsonObject)(root["providers"] = new JsonObject());
            var saved = providers[name] as JsonObject ?? (JsonObject)(providers[name] = new JsonObject());
            saved["endpoint"] = url;
            if (env is null)
                saved.Remove("apiKeyEnv");
            else
                saved["apiKeyEnv"] = env;

            var provider = ProviderObject(root);
            provider.Clear();
            provider["model"] = reference;
        });
        return reference;
    }

    /// <summary>Finds the key in the environment or keychain, or asks for one and stores it in the keychain.</summary>
    async Task<(bool Ok, string? Key)> KeyAsync(string env, bool required)
    {
        if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } fromEnv)
        {
            io.Line($"Using ${env} from your environment.");
            return (true, fromEnv);
        }

        var account = Providers.Providers.KeychainAccount(env);
        var stored = await TryGetAsync(account);
        if (stored is not null)
        {
            var keep = io.Select($"A key for {env} is already saved in the keychain.", ["Keep it", "Replace it"]);
            if (keep is null)
                return (false, null);
            if (keep == "Keep it")
                return (true, stored);
        }

        var pasted = io.AskSecret($"Paste the API key to save it in the OS keychain{(required ? "" : " (blank if the server needs none)")}: ");
        if (pasted is null)
            return (false, null);
        pasted = pasted.Trim();
        if (pasted.Length == 0)
        {
            if (required)
                io.Line($"No key saved. Set ${env} before starting anchor.");
            return (true, null);
        }

        try
        {
            await keychain.SetAsync(account, pasted);
            io.Line($"Saved in the keychain. ${env} still takes precedence when it's set.");
        }
        catch (InvalidOperationException e)
        {
            io.Line($"Couldn't save to the keychain: {e.Message} Set ${env} instead.");
        }
        return (true, pasted);
    }

    async Task<string?> TryGetAsync(string account)
    {
        try
        {
            return await keychain.GetAsync(account);
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    async Task<List<string>?> ListAsync(string type, string endpoint, string? key, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct, bool quiet = false)
    {
        try
        {
            return await Providers.Providers.ListModelsAsync(http, type, endpoint, key, headers, ct);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            if (!quiet)
                io.Line($"Couldn't list models: {e.Message} You can type a model name instead.");
            return null;
        }
    }

    string? PickModel(List<string>? models, string? fallback)
    {
        if (models is not { Count: > 0 })
            return Ask("Model", fallback);
        var preferred = fallback is not null && models.Contains(fallback) ? fallback : null;
        return io.Select($"Model ({models.Count} available)", models, preferred, allowTyped: true);
    }

    /// <summary>Asks until there's an answer; a blank answer takes the default when there is one.</summary>
    string? Ask(string prompt, string? fallback = null)
    {
        while (true)
        {
            var answer = io.Ask(fallback is { Length: > 0 } ? $"{prompt} [{fallback}]: " : $"{prompt}: ")?.Trim();
            if (answer is null)
                return null;
            if (answer.Length > 0)
                return answer;
            if (fallback is not null)
                return fallback;
        }
    }

    JsonObject Load()
    {
        if (!File.Exists(configPath))
            return [];
        return JsonNode.Parse(File.ReadAllText(configPath), NodeOptions, DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException($"{configPath} is not a JSON object.");
    }

    /// <summary>Edits the config in place, keeping every other setting; comments can't survive, so the original is kept beside it.</summary>
    void Save(Action<JsonObject> edit)
    {
        var root = Load();
        if (File.Exists(configPath))
        {
            var text = File.ReadAllText(configPath);
            if (text.Contains("//") || text.Contains("/*"))
            {
                File.Copy(configPath, configPath + ".bak", overwrite: true);
                io.Line($"Your config's comments can't be kept; the original is saved as {configPath}.bak.");
            }
        }
        edit(root);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    static JsonObject ProviderObject(JsonObject root)
    {
        var key = root.Select(p => p.Key).FirstOrDefault(k => k.Equals("provider", StringComparison.OrdinalIgnoreCase)) ?? "provider";
        return root[key] as JsonObject ?? (JsonObject)(root[key] = new JsonObject());
    }

    static string DefaultName(Uri uri)
    {
        if (uri.IsLoopback)
            return "local";
        var label = uri.Host.Split('.').FirstOrDefault(l => l is not ("api" or "www")) ?? uri.Host;
        return label.Length > 0 && char.IsAsciiLetter(label[0]) ? label.ToLowerInvariant() : "work";
    }

    static string EnvName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        return sb + "_API_KEY";
    }

    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}

/// <summary>The terminal side of the wizard; a pasted key is echoed as dots.</summary>
public sealed class ConsoleSetupIO : ISetupIO
{
    public string? Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }

    public string? AskSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Console.WriteLine();
                return null;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
                Console.Write('•');
            }
        }
        Console.WriteLine();
        return secret.ToString();
    }

    public string? Select(string title, IReadOnlyList<string> choices, string? selected = null, bool allowTyped = false) =>
        Picker.Choose(title, choices, selected, allowTyped);

    public void Line(string text = "") => Console.WriteLine(text);
}
