using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

/// <summary>Which file-editing tools each model is offered: apply_patch for OpenAI's models, write_file and edit_file otherwise.</summary>
public class EditFormatTests
{
    static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    static Toolbox AllTools() => new([Tool("read_file"), Tool("write_file"), Tool("edit_file"), Tool("apply_patch")]);

    [Theory]
    [InlineData("gpt-5.1", true)]
    [InlineData("openai/gpt-5", true)]
    [InlineData("gpt-5.1-codex-mini", true)]
    [InlineData("o3", true)]
    [InlineData("o4-mini", true)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("grok-4", false)]
    [InlineData("ollama/qwen3-coder", false)]
    [InlineData(null, false)]
    public void UsesPatches(string? model, bool patches) => Assert.Equal(patches, Toolbox.UsesPatches(model));

    [Fact]
    public void EachModelIsOfferedOneFormat()
    {
        var tools = AllTools();

        Assert.Equal(["read_file", "apply_patch"], tools.DeclarationsFor("gpt-5.1").Select(t => t.Name));
        Assert.Equal(["read_file", "write_file", "edit_file"], tools.DeclarationsFor("claude-opus-5").Select(t => t.Name));
    }

    [Fact]
    public void AToolboxWithOneFormat_KeepsIt()
    {
        var tools = new Toolbox([Tool("write_file"), Tool("edit_file")]);

        Assert.Equal(["write_file", "edit_file"], tools.DeclarationsFor("gpt-5.1").Select(t => t.Name));
    }

    [Fact]
    public void NamingAnEditToolInASubset_BringsBothFormats()
    {
        Assert.Equal(["apply_patch", "edit_file", "read_file", "write_file"], AllTools().Subset(["read_file", "edit_file"]).Names);
        Assert.Equal(["read_file"], AllTools().Subset(["read_file"]).Names);
    }

    [Fact]
    public async Task TheAgentOffersTheFormatOfTheModelItUses()
    {
        var client = new FakeChatClient().Text("one").Text("two");
        var agent = new Agent(client, AllTools(), "system", _ => { }, new ChatOptions { ModelId = "claude-sonnet-5" });

        await agent.RunTurnAsync("hi", default);
        agent.Use(client, new ChatOptions { ModelId = "gpt-5.1" });
        await agent.RunTurnAsync("hi", default);

        Assert.Contains("edit_file", client.Tools[0]);
        Assert.DoesNotContain("apply_patch", client.Tools[0]);
        Assert.Contains("apply_patch", client.Tools[1]);
        Assert.DoesNotContain("edit_file", client.Tools[1]);
    }
}
