using System.Formats.Tar;
using System.IO.Compression;
using Anchor.Cli;

namespace Anchor.Tests;

public sealed class UpdateTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anchor-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("0.9.0", "0.8.1", true)]
    [InlineData("0.10.0", "0.9.3", true)]
    [InlineData("0.8.1", "0.8.1+abcdef1", false)]
    [InlineData("0.8.0", "0.8.1", false)]
    [InlineData("0.9.0-rc.1", "0.8.1", false)]
    [InlineData("0.9.0", "0.8.2-alpha.0.4+abcdef1", false)]
    [InlineData("0.9.0", "0.0.0", true)]
    [InlineData("garbage", "0.8.1", false)]
    public void IsNewer_ComparesReleases_AndNeverPreReleases(string candidate, string current, bool expected) =>
        Assert.Equal(expected, Update.IsNewer(candidate, current));

    [Fact]
    public void Expected_FindsTheArchivesChecksum()
    {
        var sums = "aaa  anchor-0.9.0-linux-x64.tar.gz\nbbb *anchor-0.9.0-win-x64.zip\r\nccc  anchor-0.9.0-linux-arm64.tar.gz\n";

        Assert.Equal("aaa", Update.Expected(sums, "anchor-0.9.0-linux-x64.tar.gz"));
        Assert.Equal("bbb", Update.Expected(sums, "anchor-0.9.0-win-x64.zip"));
        Assert.Null(Update.Expected(sums, "anchor-0.9.0-osx-arm64.tar.gz"));
    }

    [Theory]
    [InlineData("/opt/homebrew/Cellar/anchor/0.8.1/bin/anchor", "brew upgrade anchor")]
    [InlineData("/home/linuxbrew/.linuxbrew/Cellar/anchor/0.8.1/bin/anchor", "brew upgrade anchor")]
    [InlineData(@"C:\Users\me\scoop\apps\anchor\current\anchor.exe", "scoop update anchor")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Fuseraft.Anchor_x\anchor.exe", "winget upgrade Fuseraft.Anchor")]
    [InlineData("/home/me/.local/bin/anchor", null)]
    [InlineData(@"C:\Users\me\AppData\Local\anchor\bin\anchor.exe", null)]
    public void PackageManager_IsLeftToUpdateWhatItInstalled(string path, string? command) =>
        Assert.Equal(command, Update.PackageManager(path));

    [Fact]
    public void Extract_TakesTheBinaryOutOfATarball()
    {
        using var archive = new MemoryStream();
        using (var tar = new TarWriter(new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true)))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "out/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "out/anchor") { DataStream = new MemoryStream("new"u8.ToArray()) });
        }
        using var binary = new MemoryStream();

        Assert.True(Update.Extract(archive.ToArray(), zip: false, binary));
        Assert.Equal("new"u8.ToArray(), binary.ToArray());
    }

    [Fact]
    public void Extract_TakesTheBinaryOutOfAZip_OrSaysItIsMissing()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("anchor.exe").Open())
            entry.Write("new"u8);
        using var binary = new MemoryStream();

        Assert.True(Update.Extract(archive.ToArray(), zip: true, binary));
        Assert.Equal("new"u8.ToArray(), binary.ToArray());

        using var empty = new MemoryStream();
        using (new ZipArchive(empty, ZipArchiveMode.Create, leaveOpen: true)) { }
        Assert.False(Update.Extract(empty.ToArray(), zip: true, new MemoryStream()));
    }

    [Fact]
    public void Swap_ReplacesTheBinary_KeepingItsPermissions()
    {
        var exe = Path.Combine(_dir, "anchor");
        var staged = Path.Combine(_dir, "staged");
        File.WriteAllText(exe, "old");
        File.WriteAllText(staged, "new");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, (UnixFileMode)Convert.ToInt32("755", 8));

        Update.Swap(staged, exe);

        Assert.Equal("new", File.ReadAllText(exe));
        if (OperatingSystem.IsWindows())
            Assert.Equal("old", File.ReadAllText(exe + ".old"));
        else
            Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), File.GetUnixFileMode(exe));
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir), f => f.EndsWith(".new"));
    }
}
