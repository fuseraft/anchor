using Anchor.Core;
using Anchor.Tools;

namespace Anchor.Tests;

public sealed class UntilTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-until-").FullName;
    readonly List<AgentEvent> _events = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    (Agent, Gate) Build(FakeChatClient client, IApprover? approver = null)
    {
        var workspace = new Workspace(_root);
        var gate = new Gate(workspace, new Policy(workspace), approver ?? new FakeApprover(Answer.Yes), _events.Add);
        var toolbox = new Toolbox([.. new FileTools(gate).All(), .. new EditTools(gate).All()]);
        return (new Agent(client, toolbox, "system", _events.Add), gate);
    }

    List<CheckRan> Checks => [.. _events.OfType<CheckRan>()];

    [Fact]
    public async Task KeepsGoingUntilTheCheckPasses_AndShowsTheModelWhatFailed()
    {
        var client = new FakeChatClient()
            .Call("write_file", new { path = "wrong.txt", content = "x" }).Text("done")
            .Call("write_file", new { path = "done.txt", content = "x" }).Text("really done");
        var (agent, gate) = Build(client);

        var result = await Until.RunAsync(agent, gate, "test -f done.txt || { echo 'done.txt missing'; exit 1; }", "make done.txt", default);

        Assert.Equal((TurnEnd.Completed, CheckEnd.Passed), result);
        Assert.Equal([false, true], Checks.Select(c => c.Passed));
        var followUp = client.Requests[2][^1].Text;
        Assert.StartsWith("[anchor] The check", followUp);
        Assert.Contains("done.txt missing", followUp);
    }

    [Fact]
    public async Task ARoundThatChangesNoFiles_EndsTheLoop()
    {
        var (agent, gate) = Build(new FakeChatClient().Text("Which file did you mean?"));

        var result = await Until.RunAsync(agent, gate, "false", "fix it", default);

        Assert.Equal((TurnEnd.Completed, CheckEnd.NoChanges), result);
        Assert.Single(Checks);
    }

    [Fact]
    public async Task StopsAfterMaxRounds()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < Until.MaxRounds; i++)
            client.Call("write_file", new { path = $"f{i}.txt", content = "x" }).Text("try again");
        var (agent, gate) = Build(client);

        var result = await Until.RunAsync(agent, gate, "false", "fix it", default);

        Assert.Equal((TurnEnd.Completed, CheckEnd.OutOfRounds), result);
        Assert.Equal(Until.MaxRounds, Checks.Count);
    }

    [Fact]
    public async Task TheCheckIsTheUsersOwn_SoItNeverAsks()
    {
        var approver = new FakeApprover(Answer.No);
        var (agent, gate) = Build(new FakeChatClient().Text("ok"), approver);

        await Until.RunAsync(agent, gate, "touch checked.txt", "hi", default);

        Assert.True(File.Exists(Path.Combine(_root, "checked.txt")));
        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task ATurnThatDoesntComplete_SkipsTheCheck()
    {
        var (agent, gate) = Build(new FakeChatClient().Throws(new HttpRequestException("401")));

        var result = await Until.RunAsync(agent, gate, "touch checked.txt", "hi", default);

        Assert.Equal((TurnEnd.Error, (CheckEnd?)null), result);
        Assert.Empty(Checks);
    }
}
