using Anchor.Core;
using Anchor.Tools;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

public sealed class SkillToolTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-skill-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    (Skill Skill, string Dir) MakeSkill()
    {
        var dir = Path.Combine(_root, "skills", "pdf-tools");
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: pdf-tools\ndescription: PDFs\n---\n# PDF\nRun scripts/extract.py.\n");
        File.WriteAllText(Path.Combine(dir, "scripts", "extract.py"), "print('x')");
        File.WriteAllText(Path.Combine(dir, ".env"), "KEY=sk-fake");
        return (new Skill("pdf-tools", "PDFs", dir), dir);
    }

    static AgentTools Tools(params Skill[] skills) =>
        new(new SubAgentRunner(new Toolbox([]), "", _ => { }, _ => Task.FromResult<(IChatClient, ChatOptions)>((new FakeChatClient(), new ChatOptions()))), [], skills);

    [Fact]
    public void LoadSkill_ReturnsBodyDirectoryAndFiles_WithoutSecrets()
    {
        var (skill, dir) = MakeSkill();

        var text = Tools(skill).LoadSkill("pdf-tools");

        Assert.StartsWith($"# Skill: pdf-tools\nDirectory: {dir}\n", text);
        Assert.Contains("scripts/extract.py", text);
        Assert.DoesNotContain(".env", text);
        Assert.DoesNotContain("name: pdf-tools", text);
        Assert.EndsWith("# PDF\nRun scripts/extract.py.", text);
    }

    [Fact]
    public void LoadSkill_UnknownNameListsTheAvailableOnes()
    {
        var (skill, _) = MakeSkill();

        Assert.Contains("Available: pdf-tools", Assert.Throws<ToolException>(() => Tools(skill).LoadSkill("nope")).Message);
    }

    [Fact]
    public async Task SkillFiles_AreReadableWithoutApproval_ButSecretsStayDenied()
    {
        var (skill, dir) = MakeSkill();
        var workspace = new Workspace(Directory.CreateDirectory(Path.Combine(_root, "work")).FullName);
        var approver = new FakeApprover(Answer.No);
        var files = new FileTools(new Gate(workspace, new Policy(workspace, readRoots: [dir]), approver, _ => { }));

        Assert.Contains("print('x')", await files.ReadFile(Path.Combine(dir, "scripts", "extract.py")));
        await Assert.ThrowsAsync<ToolException>(() => files.ReadFile(Path.Combine(dir, ".env")));
        await Assert.ThrowsAsync<ToolException>(() => files.ReadFile(Path.Combine(_root, "skills")) );
        Assert.Single(approver.Requests);
    }

    [Fact]
    public void SystemPrompt_ListsSkills()
    {
        var (skill, _) = MakeSkill();

        var prompt = SystemPrompt.Build(new Workspace(_root), new DateOnly(2026, 9, 28), [skill]);

        Assert.Contains("# Skills", prompt);
        Assert.Contains("- pdf-tools: PDFs", prompt);
        Assert.DoesNotContain("# Skills", SystemPrompt.Build(new Workspace(_root), new DateOnly(2026, 9, 28)));
    }
}
