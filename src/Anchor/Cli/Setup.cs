using System.Net;
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

    /// <summary>How setup is going, set off from the questions: indented, and marked ✓ or ✗ when <paramref name="ok"/>
    /// says it went well or badly. Each line of <paramref name="text"/> is printed as its own line.</summary>
    void Note(string text, bool? ok = null)
    {
        foreach (var (mark, line) in NoteLines(text, ok))
            Line(mark + line);
    }

    /// <summary>A note's lines with their indent and mark; lines after the first line up under the first one's text.</summary>
    static IEnumerable<(string Mark, string Line)> NoteLines(string text, bool? ok)
    {
        var first = ok switch { true => "  ✓ ", false => "  ✗ ", null => "  " };
        var rest = new string(' ', first.Length);
        return text.Split('\n').Select((line, i) => (i == 0 ? first : rest, line));
    }
}

/// <summary><c>anchor setup</c>: choose a provider, save its key, pick a model, and write config.json.</summary>
public sealed class Setup(ISetupIO io, IKeychain keychain, CredentialsFile credentials, HttpClient http, string configPath)
{
    const string AnotherServer = "Another server: LiteLLM, Ollama, vLLM or anything OpenAI-compatible";

    sealed record Service(string Label, string Type, string Endpoint, string ApiKeyEnv, string[] Prefixes, string? DefaultModel, string KeyUrl);

    static readonly Service[] Services =
    [
        new("Anthropic", "anthropic", "https://api.anthropic.com", "ANTHROPIC_API_KEY", ["claude-"], "claude-sonnet-5",
            "https://platform.claude.com/settings/keys"),
        new("OpenAI", "openai", "https://api.openai.com/v1", "OPENAI_API_KEY", ["gpt-", "o1", "o3", "o4"], null,
            "https://platform.openai.com/api-keys"),
        new("xAI", "openai", "https://api.x.ai/v1", "XAI_API_KEY", ["grok-"], "grok-4.5", "https://console.x.ai"),
    ];

    /// <summary>A server's model list; <c>Rejected</c> when it refused the key, <c>Error</c> when it couldn't be read for another reason.</summary>
    sealed record Listing(List<string>? Models, string? Rejected = null, string? Error = null);

    /// <summary>True when nothing names a model, so a first interactive run should offer setup.</summary>
    public static bool Needed(Options options, Config config) =>
        options.Model is null && config.Provider.Model is null && !Providers.Providers.HasDefaultModel();

    /// <summary>Runs the wizard and saves the result. Returns the model to use, or null if the person stopped.</summary>
    public async Task<string?> RunAsync(CancellationToken ct = default)
    {
        io.Line($"Your choices are saved in {Renderer.ShortPath(configPath)}.");
        io.Line();
        string[] sources = [.. Services.Select(s => s.Label), AnotherServer];
        var choice = io.Select("Where do your models come from?", sources);
        if (choice is null)
            return null;
        io.Line();
        var service = Services.FirstOrDefault(s => s.Label == choice);
        var model = service is not null ? await BuiltInAsync(service, ct) : await CustomAsync(ct);
        if (model is null)
            return null;

        io.Line();
        PickTheme();
        io.Line();
        io.Note($"All set: anchor will use {model}.\nRun anchor setup to change it.", ok: true);
        return model;
    }

    /// <summary>
    /// /model's picker: the models on the server <paramref name="current"/> comes from, with it chosen, using the key
    /// anchor already has. The pick is saved as the default. Returns it as /model takes it (<c>name/model</c> on a named
    /// server), or null if the person stopped.
    /// </summary>
    public async Task<string?> PickModelAsync(ProviderSettings current, CancellationToken ct = default)
    {
        var service = current.Via is null
            ? Services.FirstOrDefault(s => s.ApiKeyEnv == current.ApiKeyEnv && (current.Endpoint is null || current.Endpoint == s.Endpoint))
            : null;
        var key = current.ApiKeyEnv is not { } env ? null
            : Environment.GetEnvironmentVariable(env) is { Length: > 0 } fromEnv ? fromEnv
            : await StoredAsync(env);
        var listing = (current.Endpoint ?? service?.Endpoint) is { } endpoint
            ? await ListAsync(current.Provider, endpoint, key, current.Headers, ct)
            : new Listing(null);
        if (listing.Rejected is not null)
        {
            io.Note($"The key for {current.ApiKeyEnv} was rejected ({listing.Rejected}).\nRun /setup to replace it.", ok: false);
            return null;
        }
        if (service is not null)
            listing = listing with { Models = listing.Models?.Where(m => service.Prefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList() };

        var model = PickModel(listing, current.Model);
        if (model is null)
            return null;
        var reference = current.Via is null ? model : $"{current.Via}/{model}";
        Save(root => ProviderObject(root)["model"] = reference);
        return reference;
    }

    // Comes after the model is saved, so stopping here only keeps the theme the config already names.
    void PickTheme()
    {
        var key = Load().Select(p => p.Key).FirstOrDefault(k => k.Equals("theme", StringComparison.OrdinalIgnoreCase)) ?? "theme";
        var current = Theme.Named(Load()[key]?.GetValue<string>() ?? "") ?? Theme.BuiltIn[0];
        var width = Theme.BuiltIn.Max(t => t.Name.Length) + 2;
        var labels = Theme.BuiltIn.ToDictionary(t => t.Name.PadRight(width) + t.Description);
        var choice = io.Select("Which colors? (Esc keeps the current ones)", [.. labels.Keys], current.Name.PadRight(width) + current.Description);
        if (choice is null || !labels.TryGetValue(choice, out var theme) || theme == current)
            return;
        Save(root => root[key] = theme.Name);
    }

    async Task<string?> BuiltInAsync(Service service, CancellationToken ct)
    {
        var listing = await KeyAsync(service.ApiKeyEnv, service.KeyUrl, required: true,
            key => ListAsync(service.Type, service.Endpoint, key, null, ct));
        if (listing is null)
            return null;

        var models = listing.Models?.Where(m => service.Prefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
        var model = PickModel(listing with { Models = models }, service.DefaultModel);
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
            io.Note($"'{url}' is not an http or https URL.", ok: false);
            return null;
        }

        var existing = Load()["providers"]?.AsObject();
        var name = Ask("Name for this server; you'll pick its models as <name>/<model>", DefaultName(uri));
        if (name is null)
            return null;
        if (name.Contains('/') || name.Any(char.IsWhiteSpace))
        {
            io.Note("The name can't contain '/' or spaces.", ok: false);
            return null;
        }

        var entry = existing?[name]?.AsObject();
        var type = entry?["type"]?.GetValue<string>() ?? "openai";
        var headers = entry?["headers"]?.Deserialize<Dictionary<string, string>>();
        var envDefault = entry?["apiKeyEnv"]?.GetValue<string>() ?? EnvName(name);
        var env = Ask("Variable that holds its API key (none if it needs no key)", envDefault);
        if (env is null)
            return null;
        env = env.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : env;

        // An OpenAI-compatible server given without /v1 usually has its API there.
        async Task<Listing> List(string? key)
        {
            var listing = await ListAsync(type, url, key, headers, ct);
            if (listing.Models is null && listing.Rejected is null && type == "openai" && !url.EndsWith("/v1", StringComparison.Ordinal))
            {
                var v1 = await ListAsync(type, url + "/v1", key, headers, ct);
                if (v1.Error is null)
                {
                    url += "/v1";
                    return v1;
                }
            }
            return listing;
        }

        var listing = env is null ? await List(null) : await KeyAsync(env, null, required: false, List);
        if (listing is null)
            return null;

        var model = PickModel(listing, null);
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

    /// <summary>Finds the key in the environment, the keychain or the credentials file, or asks for one, and tries it by
    /// listing the server's models. A key the server rejects is asked for again, and a pasted key is saved only once it
    /// isn't rejected. Returns the listing, or null if the person stopped.</summary>
    async Task<Listing?> KeyAsync(string env, string? keyUrl, bool required, Func<string?, Task<Listing>> list)
    {
        if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } fromEnv)
        {
            io.Note($"Using ${env} from your environment.", ok: true);
            var listing = await list(fromEnv);
            if (listing.Rejected is null)
                return listing;
            io.Note($"The key in ${env} was rejected ({listing.Rejected}).\nFix or unset ${env}, then run anchor setup again.", ok: false);
            return null;
        }

        if (await StoredAsync(env) is { } stored)
        {
            var keep = io.Select($"A key for {env} is already saved.", ["Keep it", "Replace it"]);
            if (keep is null)
                return null;
            if (keep == "Keep it")
            {
                var listing = await list(stored);
                if (listing.Rejected is null)
                    return listing;
                io.Note($"The saved key was rejected ({listing.Rejected}).", ok: false);
            }
        }

        if (keyUrl is not null)
            io.Note($"You can create a key at {keyUrl}");
        while (true)
        {
            var pasted = io.AskSecret(required ? "Paste your API key: " : "Paste the API key (blank if the server needs none): ")?.Trim();
            if (pasted is null)
                return null;
            if (pasted.Length == 0)
            {
                if (!required)
                    return await list(null);
                io.Note($"No key entered. Run anchor setup again when you have one,\nor set ${env}.");
                return null;
            }

            var listing = await list(pasted);
            if (listing.Rejected is not null)
            {
                io.Note($"That key was rejected ({listing.Rejected}).\nPaste it again, or leave it blank to stop.", ok: false);
                continue;
            }
            await SaveKeyAsync(env, pasted);
            return listing;
        }
    }

    async Task<string?> StoredAsync(string env)
    {
        try
        {
            if (await keychain.GetAsync(Providers.Providers.KeychainAccount(env)) is { } key)
                return key;
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException) { }
        try
        {
            return credentials.Get(env);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves to the OS keychain, or to the credentials file where there is none.</summary>
    async Task SaveKeyAsync(string env, string key)
    {
        try
        {
            await keychain.SetAsync(Providers.Providers.KeychainAccount(env), key);
            io.Note("Saved the key in your OS keychain.", ok: true);
            return;
        }
        catch (InvalidOperationException) { }
        try
        {
            credentials.Set(env, key);
            io.Note($"Saved the key in {Renderer.ShortPath(credentials.Path)}, which only you can read.\n" +
                "There's no OS keychain here to keep it in.", ok: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            io.Note($"Couldn't save the key: {e.Message}\nSet ${env} before starting anchor.", ok: false);
        }
    }

    async Task<Listing> ListAsync(string type, string endpoint, string? key, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        try
        {
            return new(await Providers.Providers.ListModelsAsync(http, type, endpoint, key, headers, ct));
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new(null, Rejected: $"{(int)e.StatusCode} {e.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            return new(null, Error: e.Message);
        }
    }

    string? PickModel(Listing listing, string? fallback)
    {
        io.Line();
        if (listing.Models is not { Count: > 0 } models)
        {
            if (listing.Error is not null)
                io.Note($"Couldn't list models: {listing.Error}\nYou can type a model name instead.", ok: false);
            return Ask("Model", fallback);
        }
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
                io.Note($"Your config's comments can't be kept, so the original is saved\nas {Renderer.ShortPath(configPath)}.bak.");
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
    readonly Renderer _renderer = Renderer.ForConsole();

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

    public void Note(string text, bool? ok = null) => SetupNotes.Write(_renderer, Line, text, ok);
}

/// <summary>Notes in color: a green ✓, a red ✗, or dim text when the note is neither.</summary>
static class SetupNotes
{
    public static void Write(Renderer renderer, Action<string> line, string text, bool? ok)
    {
        foreach (var (mark, part) in ISetupIO.NoteLines(text, ok))
            line(ok switch { true => renderer.Success(mark) + part, false => renderer.Error(mark) + part, null => mark + renderer.Muted(part) });
    }
}
