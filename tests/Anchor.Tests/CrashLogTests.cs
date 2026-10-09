using System.ClientModel;
using Anchor.Cli;
using Anchor.Core;

namespace Anchor.Tests;

public sealed class CrashLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Directory.CreateTempSubdirectory("anchor-crash-").FullName, "logs");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);

    static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    [Fact]
    public void Write_RecordsTheBugAndWhatWasRunning_ForTheUserOnly()
    {
        var path = CrashLog.Write(Thrown(), _dir);

        var text = File.ReadAllText(path!);
        Assert.StartsWith($"anchor {Options.Version} (", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.Contains("at Anchor.Tests.CrashLogTests.Thrown()", text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path!));
    }

    [Fact]
    public void Write_KeepsTheNewestTen()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(_dir, $"crash-20200101-0000{i:00}-aaaaaa.log"), "old");

        var path = CrashLog.Write(Thrown(), _dir);

        var kept = Directory.GetFiles(_dir, "crash-*.log").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(10, kept.Count);
        Assert.Equal(path, kept[^1]);
        Assert.DoesNotContain(kept, f => f.EndsWith("crash-20200101-000000-aaaaaa.log"));
    }

    public static TheoryData<Exception, bool> Failures => new()
    {
        { new InvalidOperationException("bug"), true },
        { new NullReferenceException(), true },
        { new HttpRequestException("connection refused"), false },
        { new InvalidOperationException("wrapped", new HttpRequestException("reset")), false },
        { new TaskCanceledException("timed out"), false },
        { new AnchorException("OPENAI_API_KEY is not set"), false },
        { new ClientResultException("429 Too Many Requests"), false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void IsBug_LeavesOutProviderNetworkAndUserProblems(Exception e, bool bug) => Assert.Equal(bug, CrashLog.IsBug(e));
}
