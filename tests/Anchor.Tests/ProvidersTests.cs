using System.Net;
using System.Net.Sockets;
using System.Text;
using Anchor.Core;
using Anchor.Providers;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

public class ProvidersTests
{
    [Theory]
    [InlineData("claude-sonnet-5", "anthropic", null, "ANTHROPIC_API_KEY")]
    [InlineData("grok-4.5", "openai", "https://api.x.ai/v1", "XAI_API_KEY")]
    [InlineData("gpt-4.1", "openai", null, "OPENAI_API_KEY")]
    public void Resolve_InfersProviderFromModel(string model, string provider, string? endpoint, string env)
    {
        var s = Providers.Providers.Resolve(model);

        Assert.Equal(new ProviderSettings(provider, model, endpoint, env), s);
    }

    [Fact]
    public void Resolve_ExplicitConfigWins()
    {
        var s = Providers.Providers.Resolve("grok-4.5", endpoint: "http://localhost:9000/v1", apiKeyEnv: "LOCAL_KEY");

        Assert.Equal("http://localhost:9000/v1", s.Endpoint);
        Assert.Equal("LOCAL_KEY", s.ApiKeyEnv);
    }

    [Fact]
    public void Resolve_UnknownModelNeedsEndpointAndKey()
    {
        var e = Assert.Throws<AnchorException>(() => Providers.Providers.Resolve("llama-3"));
        Assert.Contains("provider.endpoint", e.Message);

        var s = Providers.Providers.Resolve("llama-3", endpoint: "http://localhost:11434/v1", apiKeyEnv: "OLLAMA_KEY");
        Assert.Equal("openai", s.Provider);
    }

    [Fact]
    public void Create_MissingKey_NamesTheVariable()
    {
        var e = Assert.Throws<AnchorException>(() =>
            Providers.Providers.Create(new ProviderSettings("openai", "gpt-4.1", null, "ANCHOR_TEST_UNSET_KEY")));
        Assert.Contains("ANCHOR_TEST_UNSET_KEY", e.Message);
    }

    static readonly Dictionary<string, CustomProvider> Work = new()
    {
        ["work"] = new() { Endpoint = "https://litellm.example/v1", ApiKeyEnv = "LITELLM_API_KEY", ContextWindow = 64_000 },
    };

    [Fact]
    public void Resolve_CustomProviderPrefix_KeepsTheRestOfTheName()
    {
        var s = Providers.Providers.Resolve("work/anthropic/claude-sonnet-5", custom: Work);

        Assert.Equal("openai", s.Provider);
        Assert.Equal("anthropic/claude-sonnet-5", s.Model);
        Assert.Equal("https://litellm.example/v1", s.Endpoint);
        Assert.Equal("LITELLM_API_KEY", s.ApiKeyEnv);
        Assert.Equal("work", s.Via);
        Assert.Equal(64_000, Providers.Providers.ContextWindow(s, null));
        Assert.Equal(10_000, Providers.Providers.ContextWindow(s, 10_000));
    }

    [Fact]
    public void Resolve_CustomProvider_ContextWindowFallsBackToTheModelFamily()
    {
        var custom = new Dictionary<string, CustomProvider> { ["work"] = new() { Endpoint = "https://litellm.example/v1" } };

        var s = Providers.Providers.Resolve("work/claude-sonnet-5", custom: custom);

        Assert.Equal(200_000, Providers.Providers.ContextWindow(s, null));
    }

    [Theory]
    [InlineData("work/anthropic.claude-sonnet-5", "anthropic.claude-sonnet-5", 200_000)]
    [InlineData("work/us.anthropic.claude-sonnet-5-v1:0", "us.anthropic.claude-sonnet-5-v1:0", 200_000)]
    [InlineData("work/xai.grok-4.6", "xai.grok-4.6", 256_000)]
    [InlineData("work/meta.llama4-maverick", "meta.llama4-maverick", 128_000)]
    public void Resolve_BedrockStyleNames_KeepTheirFamilyDefaults(string name, string model, long window)
    {
        var custom = new Dictionary<string, CustomProvider> { ["work"] = new() { Endpoint = "https://litellm.example/v1" } };

        var s = Providers.Providers.Resolve(name, custom: custom);

        Assert.Equal(model, s.Model);
        Assert.Equal(window, Providers.Providers.ContextWindow(s, null));
    }

    [Fact]
    public void Resolve_UnknownPrefix_IsNotACustomProvider()
    {
        var e = Assert.Throws<AnchorException>(() => Providers.Providers.Resolve("home/llama-3", custom: Work));
        Assert.Contains("providers", e.Message);
    }

    [Theory]
    [InlineData("work/", "No model")]
    [InlineData("bad/x", "no endpoint")]
    [InlineData("odd/x", "type 'gemini'")]
    public void Resolve_CustomProviderProblems(string model, string message)
    {
        var custom = new Dictionary<string, CustomProvider>(Work)
        {
            ["bad"] = new(),
            ["odd"] = new() { Endpoint = "http://x", Type = "gemini" },
        };

        var e = Assert.Throws<AnchorException>(() => Providers.Providers.Resolve(model, custom: custom));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public async Task Create_CustomProvider_SendsTheKeyAndHeaders()
    {
        Environment.SetEnvironmentVariable("ANCHOR_TEST_PROXY_KEY", "sk-proxy");
        Environment.SetEnvironmentVariable("ANCHOR_TEST_TEAM", "platform");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var custom = new Dictionary<string, CustomProvider>
        {
            ["work"] = new()
            {
                Endpoint = $"http://127.0.0.1:{port}/v1",
                ApiKeyEnv = "ANCHOR_TEST_PROXY_KEY",
                Headers = new() { ["X-Team"] = "${ANCHOR_TEST_TEAM}" },
            },
        };
        var settings = Providers.Providers.Resolve("work/gpt-4.1", custom: custom);
        var client = Providers.Providers.Create(settings);

        var request = ReadOneRequestAsync(listener);
        var response = await client.GetResponseAsync("hi", Providers.Providers.Options(settings));
        var head = await request;

        Assert.Equal("ok", response.Text);
        Assert.StartsWith("POST /v1/chat/completions", head);
        Assert.Contains("Authorization: Bearer sk-proxy", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Team: platform", head, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Options_Effort_SetsReasoning_AndRoomToThink()
    {
        var claude = Providers.Providers.Resolve("claude-sonnet-5");

        Assert.Null(Providers.Providers.Options(claude).Reasoning);
        Assert.Equal(16_000, Providers.Providers.Options(claude).MaxOutputTokens);
        Assert.Equal(16_000, Providers.Providers.Options(claude, ReasoningEffort.None).MaxOutputTokens);
        var high = Providers.Providers.Options(claude, ReasoningEffort.High);
        Assert.Equal(ReasoningEffort.High, high.Reasoning?.Effort);
        Assert.Equal(64_000, high.MaxOutputTokens);
        Assert.Null(Providers.Providers.Options(Providers.Providers.Resolve("grok-4.5"), ReasoningEffort.High).MaxOutputTokens);
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("Low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData(" xhigh ", ReasoningEffort.ExtraHigh)]
    [InlineData("default", null)]
    [InlineData(null, null)]
    public void ParseEffort_ReadsEachLevel(string? name, ReasoningEffort? effort)
    {
        Assert.Equal(effort, Providers.Providers.ParseEffort(name));
        if (effort is not null)
            Assert.Equal(name!.Trim().ToLowerInvariant(), Providers.Providers.EffortName(effort));
    }

    [Fact]
    public void ParseEffort_Unknown_NamesTheLevels()
    {
        var e = Assert.Throws<AnchorException>(() => Providers.Providers.ParseEffort("max"));
        Assert.Contains("none, low, medium, high, xhigh, default", e.Message);
    }

    [Fact]
    public async Task Effort_Anthropic_SendsAdaptiveThinkingWithTheEffort()
    {
        var request = await SendAsync("anthropic", ReasoningEffort.High, AnthropicReply);

        Assert.StartsWith("POST /v1/messages", request);
        Assert.Contains("\"thinking\":{\"type\":\"adaptive\",\"display\":\"summarized\"}", request);
        Assert.Contains("\"output_config\":{\"effort\":\"high\"", request);
        Assert.Contains("\"max_tokens\":64000", request);
    }

    [Fact]
    public async Task Effort_OpenAI_SendsReasoningEffort()
    {
        var request = await SendAsync("openai", ReasoningEffort.Low, OpenAIReply);

        Assert.StartsWith("POST /v1/chat/completions", request);
        Assert.Contains("\"reasoning_effort\":\"low\"", request);
    }

    [Fact]
    public async Task OpenAI_StreamedReasoningContent_ArrivesAsThinking()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Environment.SetEnvironmentVariable("ANCHOR_TEST_EFFORT_KEY", "sk-test");
        var custom = new Dictionary<string, CustomProvider>
        {
            ["test"] = new() { Endpoint = $"http://127.0.0.1:{port}/v1", ApiKeyEnv = "ANCHOR_TEST_EFFORT_KEY" },
        };
        var settings = Providers.Providers.Resolve("test/grok-4.7", custom: custom);
        var client = Providers.Providers.Create(settings);
        // As xAI streams it: the thinking in reasoning_content deltas, then the reply.
        string Chunk(string delta) => $$"""data: {"id":"1","object":"chat.completion.chunk","created":0,"model":"grok-4.7","choices":[{"index":0,"delta":{{delta}}}]}""" + "\n\n";
        var sse = Chunk("""{"reasoning_content":"Checking 4819","role":"assistant"}""") + Chunk("""{"reasoning_content":" for factors."}""")
            + Chunk("""{"content":"ok"}""") + "data: [DONE]\n\n";

        var request = ReadOneRequestAsync(listener, sse, "text/event-stream");
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync("hi", Providers.Providers.Options(settings, ReasoningEffort.Low)))
            updates.Add(update);
        await request;

        Assert.Equal("Checking 4819 for factors.", string.Concat(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text)));
        Assert.Equal("ok", string.Concat(updates.Select(u => u.Text)));
    }

    static async Task<string> SendAsync(string type, ReasoningEffort effort, string reply)
    {
        Environment.SetEnvironmentVariable("ANCHOR_TEST_EFFORT_KEY", "sk-test");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = type == "anthropic" ? $"http://127.0.0.1:{port}" : $"http://127.0.0.1:{port}/v1";
        var custom = new Dictionary<string, CustomProvider>
        {
            ["test"] = new() { Endpoint = endpoint, ApiKeyEnv = "ANCHOR_TEST_EFFORT_KEY", Type = type },
        };
        var settings = Providers.Providers.Resolve("test/m", custom: custom);
        var client = Providers.Providers.Create(settings);

        var request = ReadOneRequestAsync(listener, reply);
        var response = await client.GetResponseAsync("hi", Providers.Providers.Options(settings, effort));

        Assert.Equal("ok", response.Text);
        return await request;
    }

    const string OpenAIReply = """{"id":"1","object":"chat.completion","created":0,"model":"gpt-4.1","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    const string AnthropicReply = """{"id":"msg_1","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""";

    /// <summary>Answers one request with <paramref name="reply"/> and returns the request, head and body.</summary>
    static async Task<string> ReadOneRequestAsync(TcpListener listener, string reply = OpenAIReply, string type = "application/json")
    {
        using var socket = await listener.AcceptTcpClientAsync();
        var stream = socket.GetStream();
        var buffer = new byte[65536];
        var bytes = new List<byte>();
        int end;
        while ((end = Encoding.ASCII.GetString([.. bytes]).IndexOf("\r\n\r\n")) < 0)
            bytes.AddRange(buffer.AsSpan(0, await stream.ReadAsync(buffer)));
        var head = Encoding.ASCII.GetString([.. bytes])[..end];
        var length = int.Parse(head.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
        while (bytes.Count - end - 4 < length)
            bytes.AddRange(buffer.AsSpan(0, await stream.ReadAsync(buffer)));

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {Encoding.UTF8.GetByteCount(reply)}\r\nConnection: close\r\n\r\n{reply}"));
        return Encoding.UTF8.GetString([.. bytes]);
    }
}
