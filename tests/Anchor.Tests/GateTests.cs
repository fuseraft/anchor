using Anchor.Core;

namespace Anchor.Tests;

public sealed class GateTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-gate-").FullName;
    readonly string _outside = Directory.CreateTempSubdirectory("anchor-outside-").FullName;
    readonly List<AgentEvent> _events = [];

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    Gate NewGate(FakeApprover approver, bool yolo = false, bool parsesShell = true, ApprovalStore? saved = null)
    {
        var workspace = new Workspace(_root);
        return new Gate(workspace, new Policy(workspace, yolo) { ParsesShell = parsesShell }, approver, _events.Add, saved);
    }

    ApprovalStore Store(string? workspace = null) => new(Path.Combine(_outside, "approvals.json"), workspace ?? _root);

    string At(string rel) => Path.Combine(_root, rel);

    [Fact]
    public async Task Write_ShowsDiffAndWritesAfterApproval()
    {
        File.WriteAllText(At("a.txt"), "one\ntwo\n");
        var approver = new FakeApprover(Answer.Yes);
        var gate = NewGate(approver);
        gate.MarkRead(At("a.txt"));

        await gate.WriteAsync(new FileEdit(At("a.txt"), "one\ntwo\n", "one\nTWO\n"), default);

        Assert.Equal("one\nTWO\n", File.ReadAllText(At("a.txt")));
        var request = Assert.Single(approver.Requests);
        Assert.Equal("Edit a.txt", request.Title);
        Assert.Contains("-two", request.Detail);
        Assert.Contains("+TWO", request.Detail);
        Assert.Contains(new FileChanged("a.txt", 1, 1), _events);
    }

    [Fact]
    public async Task Write_DeclinedLeavesFileUntouched()
    {
        File.WriteAllText(At("a.txt"), "keep");
        var gate = NewGate(new FakeApprover(Answer.No));
        gate.MarkRead(At("a.txt"));

        var e = await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("a.txt"), "keep", "changed"), default));

        Assert.Contains("declined", e.Message);
        Assert.Equal("keep", File.ReadAllText(At("a.txt")));
        Assert.DoesNotContain(_events, ev => ev is FileChanged);
    }

    [Fact]
    public async Task Allow_PreapprovesProgramsAndToolsButNotDenials()
    {
        var approver = new FakeApprover(Answer.No);
        var gate = NewGate(approver);
        gate.Allow("touch");
        gate.Allow("mcp__docs__write");

        await gate.RunAsync("touch a", TimeSpan.FromSeconds(10), default);
        Assert.Equal("ok", await gate.CallExternalAsync("mcp__docs__write", false, "{}", _ => Task.FromResult("ok"), default));
        await Assert.ThrowsAsync<ToolException>(() => gate.RunAsync("touch b && rm c", TimeSpan.FromSeconds(10), default));
        await Assert.ThrowsAsync<ToolException>(() => gate.RunAsync("sudo touch d", TimeSpan.FromSeconds(10), default));

        Assert.True(File.Exists(At("a")));
        Assert.Single(approver.Requests);
    }

    [Fact]
    public async Task Write_AlwaysSkipsLaterPromptsInsideOnly()
    {
        var approver = new FakeApprover(Answer.Always);
        var gate = NewGate(approver);

        await gate.WriteAsync(new FileEdit(At("a.txt"), null, "1"), default);
        await gate.WriteAsync(new FileEdit(At("b.txt"), null, "2"), default);
        await gate.WriteAsync(new FileEdit(Path.Combine(_outside, "c.txt"), null, "3"), default);

        Assert.Equal(2, approver.Requests.Count);
        Assert.Null(approver.Requests[1].AlwaysLabel);
    }

    [Fact]
    public async Task Yolo_WritesWithoutAsking_ButStillDeniesSecrets()
    {
        var approver = new FakeApprover(Answer.Yes);
        var gate = NewGate(approver, yolo: true);

        await gate.WriteAsync(new FileEdit(At("a.txt"), null, "x"), default);
        Assert.Throws<ToolException>(() => gate.WritePath(".env"));
        await Assert.ThrowsAsync<ToolException>(() => gate.RunAsync("cat .env", TimeSpan.FromSeconds(5), default));

        Assert.Empty(approver.Requests);
        Assert.False(File.Exists(At(".env")));
    }

    [Fact]
    public async Task Run_ReadOnlyCommandsDontAsk_OthersDo()
    {
        var approver = new FakeApprover(Answer.Yes);
        var gate = NewGate(approver);

        var ls = await gate.RunAsync("echo hello", TimeSpan.FromSeconds(10), default);
        Assert.Contains("hello", ls);
        Assert.Contains("[exit code 0]", ls);
        Assert.Empty(approver.Requests);

        await gate.RunAsync("touch made.txt", TimeSpan.FromSeconds(10), default);
        Assert.Equal("Run: touch made.txt", Assert.Single(approver.Requests).Title);
        Assert.True(File.Exists(At("made.txt")));
    }

    [Fact]
    public async Task Run_WithoutBashRules_AsksForEverythingAndOffersNoAlways()
    {
        var approver = new FakeApprover(Answer.Always);
        var gate = NewGate(approver, parsesShell: false);

        await gate.RunAsync("echo hello", TimeSpan.FromSeconds(10), default);
        await gate.RunAsync("echo hello", TimeSpan.FromSeconds(10), default);

        Assert.Equal(2, approver.Requests.Count);
        Assert.All(approver.Requests, r => Assert.Null(r.AlwaysLabel));
    }

    [Fact]
    public async Task Run_WithoutBashRules_StillDenies()
    {
        var approver = new FakeApprover(Answer.Yes);

        var e = await Assert.ThrowsAsync<ToolException>(() => NewGate(approver, parsesShell: false).RunAsync("sudo true", TimeSpan.FromSeconds(5), default));

        Assert.StartsWith("Denied:", e.Message);
        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task Run_DeniedNeverPromptsEvenWithAYesApprover()
    {
        var approver = new FakeApprover(Answer.Yes);

        var e = await Assert.ThrowsAsync<ToolException>(() => NewGate(approver).RunAsync("sudo true", TimeSpan.FromSeconds(5), default));

        Assert.StartsWith("Denied:", e.Message);
        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task Run_AlwaysCoversOnlyTheSamePrograms()
    {
        var approver = new FakeApprover(Answer.Always);
        var gate = NewGate(approver);

        await gate.RunAsync("touch a", TimeSpan.FromSeconds(10), default);
        await gate.RunAsync("touch b", TimeSpan.FromSeconds(10), default);
        await gate.RunAsync("touch c && mkdir d", TimeSpan.FromSeconds(10), default);

        Assert.Equal(2, approver.Requests.Count);
    }

    [Fact]
    public async Task Run_ReportsExitCodeAndStderr()
    {
        var output = await NewGate(new FakeApprover(Answer.Yes)).RunAsync("echo oops >&2; exit 3", TimeSpan.FromSeconds(10), default);

        Assert.Contains("oops", output);
        Assert.Contains("[exit code 3]", output);
    }

    [Fact]
    public async Task Run_TimeoutKillsTheProcess()
    {
        var output = await NewGate(new FakeApprover(Answer.Yes)).RunAsync("echo started; sleep 30", TimeSpan.FromSeconds(1), default);

        Assert.Contains("started", output);
        Assert.Contains("timed out", output);
    }

    [Fact]
    public async Task Run_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewGate(new FakeApprover(Answer.Yes)).RunAsync("sleep 30", TimeSpan.FromSeconds(60), cts.Token));
    }

    [Fact]
    public async Task Run_MasksSecretEnvironmentValues()
    {
        Environment.SetEnvironmentVariable("ANCHOR_TEST_API_KEY", "sk-anchor-test-value-123");
        try
        {
            var output = await NewGate(new FakeApprover(Answer.Yes)).RunAsync("printenv ANCHOR_TEST_API_KEY", TimeSpan.FromSeconds(10), default);
            Assert.DoesNotContain("sk-anchor-test-value-123", output);
            Assert.Contains(Secrets.Placeholder, output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANCHOR_TEST_API_KEY", null);
        }
    }

    [Fact]
    public async Task ReadOutside_AsksAndYoloAllows()
    {
        File.WriteAllText(Path.Combine(_outside, "x.txt"), "x");
        var approver = new FakeApprover(Answer.Yes);

        await NewGate(approver).ReadPathAsync(Path.Combine(_outside, "x.txt"), default);
        Assert.Single(approver.Requests);

        await NewGate(approver, yolo: true).ReadPathAsync(Path.Combine(_outside, "x.txt"), default);
        Assert.Single(approver.Requests);
    }

    [Fact]
    public async Task Symlinks_ArePolicedByTheirTarget()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside");
        File.WriteAllText(At(".env"), "K=v");
        File.CreateSymbolicLink(At("to-outside.txt"), Path.Combine(_outside, "secret.txt"));
        Directory.CreateSymbolicLink(At("outdir"), _outside);
        File.CreateSymbolicLink(At("innocent.txt"), At(".env"));
        var approver = new FakeApprover(Answer.No);
        var gate = NewGate(approver);

        await Assert.ThrowsAsync<ToolException>(() => gate.ReadPathAsync("to-outside.txt", default));
        await Assert.ThrowsAsync<ToolException>(() => gate.ReadPathAsync("outdir/secret.txt", default));
        Assert.Equal(2, approver.Requests.Count);

        var e = await Assert.ThrowsAsync<ToolException>(() => gate.ReadPathAsync("innocent.txt", default));
        Assert.Contains("always denied", e.Message);
        Assert.Throws<ToolException>(() => gate.WritePath("innocent.txt"));
        Assert.Equal(2, approver.Requests.Count);
    }

    [Fact]
    public async Task SavedApprovals_CarryProgramsAndToolsIntoTheNextSession()
    {
        var first = new FakeApprover(Answer.Always);
        var gate = NewGate(first, saved: Store());
        await gate.RunAsync("touch a", TimeSpan.FromSeconds(10), default);
        await gate.CallExternalAsync("mcp__docs__edit", false, "{}", _ => Task.FromResult("ok"), default);
        Assert.All(first.Requests, r => Assert.EndsWith("(saved for this directory)", r.AlwaysLabel));

        var next = new FakeApprover(Answer.No);
        gate = NewGate(next, saved: Store());
        await gate.RunAsync("touch b", TimeSpan.FromSeconds(10), default);
        await gate.CallExternalAsync("mcp__docs__edit", false, "{}", _ => Task.FromResult("ok"), default);

        Assert.Empty(next.Requests);
        Assert.Equal(["touch"], gate.SavedApprovals.Programs);
        Assert.Equal(["mcp__docs__edit"], gate.SavedApprovals.Tools);
    }

    [Fact]
    public async Task SavedApprovals_SkipWritesAndInterpreters()
    {
        var approver = new FakeApprover(Answer.Always);
        var gate = NewGate(approver, saved: Store());
        await gate.WriteAsync(new FileEdit(At("a.txt"), null, "1"), default);
        await gate.RunAsync("python3 -c 'print(1)'", TimeSpan.FromSeconds(10), default);
        await gate.RunAsync("python3 -c 'print(2)'", TimeSpan.FromSeconds(10), default);

        Assert.Equal(2, approver.Requests.Count);
        Assert.DoesNotContain("saved", approver.Requests[1].AlwaysLabel);
        Assert.Empty(gate.SavedApprovals.Programs);

        var next = new FakeApprover(Answer.No);
        gate = NewGate(next, saved: Store());
        await Assert.ThrowsAsync<ToolException>(() => gate.WriteAsync(new FileEdit(At("b.txt"), null, "2"), default));
        await Assert.ThrowsAsync<ToolException>(() => gate.RunAsync("python3 -c 'print(3)'", TimeSpan.FromSeconds(10), default));
        Assert.Equal(2, next.Requests.Count);
    }

    [Fact]
    public async Task SavedApprovals_ArePerDirectoryAndCanBeForgotten()
    {
        await NewGate(new FakeApprover(Answer.Always), saved: Store()).RunAsync("touch a", TimeSpan.FromSeconds(10), default);
        Assert.Empty(Store(_outside).Load().Programs);

        var approver = new FakeApprover(Answer.No);
        var gate = NewGate(approver, saved: Store());
        gate.ForgetApprovals();
        await Assert.ThrowsAsync<ToolException>(() => gate.RunAsync("touch b", TimeSpan.FromSeconds(10), default));

        Assert.Single(approver.Requests);
        Assert.Empty(Store().Load().Programs);
    }
}
