using Anchor.Providers;

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
        var e = Assert.Throws<InvalidOperationException>(() => Providers.Providers.Resolve("llama-3"));
        Assert.Contains("provider.endpoint", e.Message);

        var s = Providers.Providers.Resolve("llama-3", endpoint: "http://localhost:11434/v1", apiKeyEnv: "OLLAMA_KEY");
        Assert.Equal("openai", s.Provider);
    }

    [Fact]
    public void Create_MissingKey_NamesTheVariable()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            Providers.Providers.Create(new ProviderSettings("openai", "gpt-4.1", null, "ANCHOR_TEST_UNSET_KEY")));
        Assert.Contains("ANCHOR_TEST_UNSET_KEY", e.Message);
    }
}
