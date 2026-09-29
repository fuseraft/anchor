using Anchor.Core;

namespace Anchor.Tests;

public sealed class DefinitionsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-defs-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void Skills_LoadValidOnesAndExplainTheRest()
    {
        Write("p/pdf-tools/SKILL.md", "---\nname: pdf-tools\ndescription: >-\n  Work with PDF\n  files.\n---\n# PDF\nSteps here.\n");
        Write("p/Wrong/SKILL.md", "---\nname: Wrong\ndescription: x\n---\n");
        Write("p/other/SKILL.md", "---\nname: mismatch\ndescription: x\n---\n");
        Write("p/nodesc/SKILL.md", "---\nname: nodesc\n---\n");
        Write("p/plain/SKILL.md", "no frontmatter\n");
        Write("p/long/SKILL.md", $"---\nname: long\ndescription: {new string('d', 1025)}\n---\n");
        Write("p/notaskill/README.md", "ignored");

        var (skills, warnings) = Definitions.LoadSkills([Path.Combine(_root, "p")]);

        var skill = Assert.Single(skills);
        Assert.Equal(("pdf-tools", "Work with PDF files."), (skill.Name, skill.Description));
        Assert.Equal(5, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("must match its directory"));
        Assert.Contains(warnings, w => w.Contains("lowercase"));
        Assert.Contains(warnings, w => w.Contains("'description' is required"));
        Assert.Contains(warnings, w => w.Contains("missing YAML frontmatter"));
        Assert.Contains(warnings, w => w.Contains("1024"));
    }

    [Fact]
    public void Skills_EarlierRootWins()
    {
        Write("project/lint/SKILL.md", "---\nname: lint\ndescription: project version\n---\n");
        Write("home/lint/SKILL.md", "---\nname: lint\ndescription: home version\n---\n");
        Write("home/extra/SKILL.md", "---\nname: extra\ndescription: only at home\n---\n");

        var (skills, _) = Definitions.LoadSkills([Path.Combine(_root, "project"), Path.Combine(_root, "home"), Path.Combine(_root, "missing")]);

        Assert.Equal(["extra", "lint"], skills.Select(s => s.Name));
        Assert.Equal("project version", skills.Single(s => s.Name == "lint").Description);
    }

    [Fact]
    public void Skills_ASymlinkToASecretIsSkipped()
    {
        var secret = Write("keys/.env", "---\nname: leak\ndescription: API_KEY=sk-fake\n---\n");
        Directory.CreateDirectory(Path.Combine(_root, "p", "leak"));
        File.CreateSymbolicLink(Path.Combine(_root, "p", "leak", "SKILL.md"), secret);

        var (skills, warnings) = Definitions.LoadSkills([Path.Combine(_root, "p")]);

        Assert.Empty(skills);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Agents_ReadToolsModelAndPrompt()
    {
        Write("a/reviewer.md", "---\nname: reviewer\ndescription: Reviews diffs\ntools: [read_file, grep, shell]\nmodel: grok-4.5\n---\nYou review code.\n");
        Write("a/writer.md", "---\nname: writer\ndescription: Writes docs\ntools: read_file, write_file\n---\nWrite docs.\n");
        Write("a/plain.md", "---\nname: plain\ndescription: Default tools\n---\n");
        Write("a/bad.md", "---\nname: bad\ndescription: x\ntools: {read_file: yes}\n---\n");

        var (agents, warnings) = Definitions.LoadAgents([Path.Combine(_root, "a")]);

        Assert.Equal(["plain", "reviewer", "writer"], agents.Select(a => a.Name));
        var reviewer = agents.Single(a => a.Name == "reviewer");
        Assert.Equal(["read_file", "grep", "shell"], reviewer.Tools);
        Assert.Equal("grok-4.5", reviewer.Model);
        Assert.Equal("You review code.", reviewer.Prompt);
        Assert.Equal(["read_file", "write_file"], agents.Single(a => a.Name == "writer").Tools);
        Assert.Null(agents.Single(a => a.Name == "plain").Tools);
        Assert.Contains("tools must be", Assert.Single(warnings));
    }
}
