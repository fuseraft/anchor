using System.Diagnostics;
using Anchor.Core;
using Anchor.Tools;

namespace Anchor.Tests;

public sealed class FileToolsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-test-").FullName;
    readonly FileTools _tools;
    readonly FakeApprover Approver = new(Answer.No);

    public FileToolsTests()
    {
        _tools = new FileTools(new Gate(new Workspace(_root), new Policy(new Workspace(_root)), Approver, _ => { }));
        Write("src/app.cs", "class App\n{\n    // TODO: token\n}\n");
        Write("src/util.cs", "static class Util { }\n");
        Write("README.md", "# demo\n");
        Write(".env", "API_TOKEN=sk-fake-secret\n");
        Write("backend/.env.local", "API_TOKEN=sk-fake-secret\n");
        Write("home/.ssh/id_ed25519", "fake key token\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    void Write(string path, string content)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task ReadFile_NumbersLines()
    {
        var text = await _tools.ReadFile("src/app.cs");

        Assert.Contains("     1\tclass App", text);
        Assert.Contains("     3\t    // TODO: token", text);
    }

    [Fact]
    public async Task ReadFile_RangeReportsHowToContinue()
    {
        Write("big.txt", string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}")));

        var text = await _tools.ReadFile("big.txt", offset: 3, limit: 2);

        Assert.Contains("3\tline3", text);
        Assert.Contains("4\tline4", text);
        Assert.DoesNotContain("line5", text);
        Assert.Contains("[6 more lines; continue with offset=5]", text);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("backend/.env.local")]
    [InlineData("home/.ssh/id_ed25519")]
    [InlineData("./backend/../.env")]
    public async Task ReadFile_DeniesSecrets(string path)
    {
        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.ReadFile(path));
        Assert.Contains("always denied", e.Message);
        Assert.Empty(Approver.Requests);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/hostname")]
    public async Task ReadFile_DeniesPathsOutsideWorkspace(string path)
    {
        var e = await Assert.ThrowsAsync<ToolException>(() => _tools.ReadFile(path));
        Assert.Contains("declined", e.Message);
        Assert.Contains("outside the workspace", Assert.Single(Approver.Requests).Title);
    }

    [Fact]
    public async Task ReadFile_SiblingWithSharedPrefix_IsOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => _tools.ReadFile(_root + "-evil/x.txt"));
        Assert.Contains("outside the workspace", Assert.Single(Approver.Requests).Title);
    }

    [Fact]
    public async Task ReadFile_RefusesBinary()
    {
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [1, 2, 0, 3]);
        await Assert.ThrowsAsync<ToolException>(() => _tools.ReadFile("blob.bin"));
    }

    [Fact]
    public async Task Grep_FindsMatchesAndSkipsSecrets()
    {
        var text = await _tools.Grep("token", ignore_case: true);

        Assert.Contains("src/app.cs:3:", text);
        Assert.DoesNotContain(".env", text);
        Assert.DoesNotContain("id_ed25519", text);
        Assert.DoesNotContain("sk-fake", text);
    }

    [Fact]
    public async Task Grep_GlobFiltersFiles()
    {
        Assert.Equal("(no matches)", await _tools.Grep("demo", glob: "*.cs"));
        Assert.Contains("README.md:1:", await _tools.Grep("demo", glob: "*.md"));
    }

    [Fact]
    public async Task Grep_InvalidRegex_IsAToolError()
    {
        await Assert.ThrowsAsync<ToolException>(() => _tools.Grep("("));
    }

    [Fact]
    public async Task Glob_MatchesAndExcludesSecrets()
    {
        Assert.Equal("src/app.cs\nsrc/util.cs", await _tools.Glob("**/*.cs"));
        Assert.Equal("(no files match)", await _tools.Glob("**/.env*"));
    }

    [Fact]
    public async Task ListDir_RespectsDepth()
    {
        var shallow = await _tools.ListDir(depth: 1);
        Assert.Contains("src/", shallow);
        Assert.DoesNotContain("src/app.cs", shallow);
        Assert.DoesNotContain(".env", shallow);

        Assert.Contains("src/app.cs", await _tools.ListDir(depth: 2));
    }

    [Fact]
    public async Task Files_HonorGitignoreInsideARepo()
    {
        Git("init", "-q");
        Write(".gitignore", "generated/\n");
        Write("generated/out.cs", "x");

        Assert.DoesNotContain("generated", await _tools.Glob("**/*.cs"));
        Assert.Contains("src/app.cs", await _tools.Glob("**/*.cs"));
    }

    [Fact]
    public async Task Files_OutsideARepo_SkipNoiseDirectories()
    {
        Write("node_modules/pkg/index.cs", "x");
        Assert.DoesNotContain("node_modules", await _tools.Glob("**/*.cs"));
    }

    void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = _root };
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
