namespace Anchor.Tests;

public class LayeringTests
{
    [Theory]
    [InlineData("Core")]
    [InlineData("Tools")]
    [InlineData("Providers")]
    public void OnlyCliWritesToTheConsole(string folder)
    {
        var dir = Path.Combine(RepoRoot(), "src", "Anchor", folder);
        var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("Console."))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ToolsReachDiskAndProcessesOnlyThroughTheGate()
    {
        string[] effects = ["File.Write", "File.Append", "File.Delete", "File.Move", "File.Copy", "File.Create", "File.OpenWrite",
            "FileMode.", "Directory.Delete", "Directory.Move", "Directory.CreateDirectory", "Process", "StreamWriter"];
        var dir = Path.Combine(RepoRoot(), "src", "Anchor", "Tools");
        var offenders = Directory.EnumerateFiles(dir, "*.cs")
            .SelectMany(f => effects.Where(File.ReadAllText(f).Contains).Select(e => $"{Path.GetFileName(f)}: {e}"))
            .ToList();

        Assert.Empty(offenders);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "anchor.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("anchor.slnx not found");
    }
}
