using System.Text.Json.Nodes;
using System.Threading.Channels;
using Anchor.Cli;
using Anchor.Core;
using Anchor.Mcp;
using Anchor.Providers;
using Anchor.Tools;
using Microsoft.Extensions.AI;

namespace Anchor.Tests;

/// <summary>A stdin whose lines the test feeds one at a time.</summary>
sealed class LineFeed : TextReader
{
    readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

    public void Send(string line) => _lines.Writer.TryWrite(line);

    public override void Close() => _lines.Writer.TryComplete();

    public override async Task<string?> ReadLineAsync()
    {
        try
        {
            return await _lines.Reader.ReadAsync();
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}

/// <summary>Collects JSON lines written by the harness.</summary>
sealed class JsonLines : StringWriter
{
    public List<JsonObject> Events
    {
        get
        {
            lock (this)
                return [.. ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject())];
        }
    }

    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> match, int count = 1)
    {
        for (var i = 0; i < 200; i++)
        {
            var found = Events.Where(match).ToList();
            if (found.Count >= count)
                return found[count - 1];
            await Task.Delay(25);
        }
        throw new TimeoutException($"Event not seen. Got: {ToString()}");
    }
}

public sealed class HeadlessTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anchor-headless-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    Harness Build(FakeChatClient client, IApprover approver, Action<AgentEvent> emit)
    {
        var workspace = new Workspace(_root);
        var usage = new SessionUsage();
        Action<AgentEvent> observed = e => { usage.Observe(e); emit(e); };
        var gate = new Gate(workspace, new Policy(workspace), approver, observed);
        var toolbox = new Toolbox([.. new FileTools(gate).All(), .. new EditTools(gate).All(), .. approver.CanAsk ? new AskTool(approver).All() : []]);
        var agent = new Agent(client, toolbox, "system", observed);
        var session = SessionLog.Create(Path.Combine(_root, ".sessions"), workspace.Root, "fake");
        var hub = new McpHub(toolbox, gate, observed, new MemoryKeychain());
        return new Harness(agent, gate, session, hub, Task.CompletedTask, new ProviderSettings("openai", "fake", null, "X"), 100_000,
            Path.Combine(_root, ".sessions"), [], [], usage, new ModelSource(_ => Task.FromResult<string?>(null), () => new Config()));
    }

    [Fact]
    public async Task Print_WritesOnlyTheFinalAnswer_AndSavesTheSession()
    {
        var stdout = new StringWriter();
        var client = new FakeChatClient().Call("read_file", new { path = "missing.txt" }).Text("the answer");
        var h = Build(client, new RefusingApprover(_ => { }), _ => { });

        var exit = await PrintMode.RunAsync(h, "question", null, stdout);

        Assert.Equal(0, exit);
        Assert.Equal("the answer\n", stdout.ToString().ReplaceLineEndings("\n"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, ".sessions")));
    }

    [Fact]
    public async Task Print_RefusesAnythingThatWouldAsk_AndSaysWhy()
    {
        var reports = new List<string>();
        var client = new FakeChatClient().Call("write_file", new { path = "a.txt", content = "x" }).Text("could not write");
        var h = Build(client, new RefusingApprover(reports.Add), _ => { });

        await PrintMode.RunAsync(h, "write a.txt", null, new StringWriter());

        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.Contains("--allow", Assert.Single(reports));
    }

    [Fact]
    public async Task Print_ExitCodeReflectsHowTheTurnEnded()
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 3; i++)
            client.Call("read_file", new { path = $"missing{i}" });

        Assert.Equal(3, await PrintMode.RunAsync(Build(client, new RefusingApprover(_ => { }), _ => { }), "x", null, new StringWriter()));
        Assert.Equal(1, await PrintMode.RunAsync(Build(new FakeChatClient().Throws(new HttpRequestException("401")), new RefusingApprover(_ => { }), _ => { }), "x", null, new StringWriter()));
    }

    [Fact]
    public async Task PrintJson_AllowedEditsAreMadeAndListed()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var client = new FakeChatClient()
            .Call("write_file", new { path = "b.txt", content = "x" })
            .Call("write_file", new { path = "a.txt", content = "y" })
            .Text("wrote both");
        var h = Build(client, new RefusingApprover(_ => { }), json.Emit);
        h.Gate.Allow("edits");

        Assert.Equal(0, await PrintMode.RunAsync(h, "x", json, new StringWriter()));
        Assert.Equal(["a.txt", "b.txt"], output.Events[^1]["files_changed"]!.AsArray().Select(f => (string)f!));
    }

    [Fact]
    public async Task Print_MaxRoundsStopsTheTurn()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var client = new FakeChatClient().Call("read_file", new { path = "missing.txt" }).Text("never reached");
        var h = Build(client, new RefusingApprover(_ => { }), json.Emit);
        h.Agent.MaxRounds = 1;

        Assert.Equal(5, await PrintMode.RunAsync(h, "x", json, new StringWriter()));
        Assert.Equal("round_limit", (string)output.Events[^1]["status"]!);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Print_TimeoutCancelsTheTurn()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var h = Build(new FakeChatClient().TextThenHang("thinking"), new RefusingApprover(_ => { }), json.Emit);

        Assert.Equal(124, await PrintMode.RunAsync(h, "x", json, new StringWriter(), timeout: TimeSpan.FromMilliseconds(200)));
        Assert.Equal("timed_out", (string)output.Events[^1]["status"]!);
    }

    [Fact]
    public async Task PrintUntil_ExitsFourWhenTheCheckNeverPasses()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var h = Build(new FakeChatClient().Text("nothing to do"), new RefusingApprover(_ => { }), json.Emit);

        Assert.Equal(4, await PrintMode.RunAsync(h, "x", json, new StringWriter(), until: "false"));
        Assert.Equal("check", (string)output.Events[^2]["type"]!);
        Assert.Equal("no_changes", (string)output.Events[^1]["check"]!);
    }

    [Fact]
    public async Task PrintJson_StreamsEventsAndEndsWithAResult()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var client = new FakeChatClient().Call("read_file", new { path = "missing.txt" }).Text("done", input: 40, output: 2);

        await PrintMode.RunAsync(Build(client, new RefusingApprover(_ => { }), json.Emit), "x", json, new StringWriter());

        var types = output.Events.Select(e => (string)e["type"]!).ToList();
        Assert.Equal(["tool_start", "tool_end", "text", "usage", "turn_end", "result"], types);
        var result = output.Events[^1];
        Assert.Equal(("completed", "done"), ((string)result["status"]!, (string)result["text"]!));
        Assert.Equal(40, (long)result["usage"]!["input"]!);
    }

    [Fact]
    public async Task JsonMode_RunsTurnsAndRoundTripsApprovals()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var approver = new JsonApprover(json);
        var stdin = new LineFeed();
        var client = new FakeChatClient().Call("write_file", new { path = "a.txt", content = "hi" }).Text("wrote it");
        var run = new JsonMode(json, approver, stdin).RunAsync(Build(client, approver, json.Emit));

        await output.WaitForAsync(e => (string)e["type"]! == "ready");
        stdin.Send("""{"type":"user_input","text":"write a.txt"}""");
        var request = await output.WaitForAsync(e => (string)e["type"]! == "approval_request");
        Assert.Equal("Create a.txt", (string)request["title"]!);
        Assert.Contains("+hi", (string)request["detail"]!);

        stdin.Send($$"""{"type":"approval_response","id":"{{request["id"]}}","answer":"yes"}""");
        await output.WaitForAsync(e => (string)e["type"]! == "ready", count: 2);
        stdin.Close();

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Contains(output.Events, e => (string)e["type"]! == "file_changed");
    }

    [Fact]
    public async Task JsonMode_ReportsAFailedSave_AndIsReadyAgain()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var approver = new JsonApprover(json);
        var stdin = new LineFeed();
        var h = Build(new FakeChatClient().Text("hello"), approver, json.Emit);
        File.WriteAllText(Path.Combine(_root, ".sessions"), "a file where the sessions directory should be");
        var run = new JsonMode(json, approver, stdin).RunAsync(h);

        await output.WaitForAsync(e => (string)e["type"]! == "ready");
        stdin.Send("""{"type":"user_input","text":"hi"}""");
        var error = await output.WaitForAsync(e => (string)e["type"]! == "error");
        await output.WaitForAsync(e => (string)e["type"]! == "ready", count: 2);
        stdin.Close();

        Assert.StartsWith("Couldn't save the session", (string)error["message"]!);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task JsonMode_RoundTripsQuestions()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var approver = new JsonApprover(json);
        var stdin = new LineFeed();
        var client = new FakeChatClient().Call("ask_user", new { question = "Which database?", options = new[] { "Postgres", "SQLite" } }).Text("ok");
        var h = Build(client, approver, json.Emit);
        var run = new JsonMode(json, approver, stdin).RunAsync(h);

        await output.WaitForAsync(e => (string)e["type"]! == "ready");
        stdin.Send("""{"type":"user_input","text":"set up storage"}""");
        var question = await output.WaitForAsync(e => (string)e["type"]! == "question");
        Assert.Equal("Which database?", (string)question["text"]!);
        Assert.Equal(["Postgres", "SQLite"], question["options"]!.AsArray().Select(o => (string)o!));
        Assert.True((bool)question["allowOther"]!);

        stdin.Send($$"""{"type":"question_response","id":"{{question["id"]}}","answer":"SQLite"}""");
        await output.WaitForAsync(e => (string)e["type"]! == "ready", count: 2);
        stdin.Close();

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var result = h.Agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("The user chose: SQLite", result.Result?.ToString());
    }

    [Fact]
    public async Task JsonMode_CancelStopsTheRunningTurn()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var approver = new JsonApprover(json);
        var stdin = new LineFeed();
        var client = new FakeChatClient().TextThenHang("thinking");
        var run = new JsonMode(json, approver, stdin).RunAsync(Build(client, approver, json.Emit));

        stdin.Send("""{"type":"user_input","text":"go"}""");
        await output.WaitForAsync(e => (string)e["type"]! == "text");
        stdin.Send("""{"type":"user_input","text":"again"}""");
        await output.WaitForAsync(e => (string)e["type"]! == "error");
        stdin.Send("""{"type":"cancel"}""");

        var end = await output.WaitForAsync(e => (string)e["type"]! == "turn_end");
        Assert.Equal("cancelled", (string)end["reason"]!);
        stdin.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task JsonMode_ReportsBadRequests()
    {
        var output = new JsonLines();
        var json = new JsonEvents(output);
        var stdin = new LineFeed();
        var approver = new JsonApprover(json);
        var run = new JsonMode(json, approver, stdin).RunAsync(Build(new FakeChatClient(), approver, json.Emit));

        stdin.Send("not json");
        stdin.Send("""{"type":"launch"}""");
        stdin.Send("""{"type":"approval_response","id":"a99","answer":"yes"}""");
        stdin.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var errors = output.Events.Where(e => (string)e["type"]! == "error").Select(e => (string)e["message"]!).ToList();
        Assert.Equal(3, errors.Count);
        Assert.StartsWith("Invalid JSON", errors[0]);
        Assert.Contains("a99", errors[2]);
    }

    [Fact]
    public void JsonEvents_MapSubAgentAndContextEvents()
    {
        var nested = JsonEvents.Map(new SubAgentEvent("finder", new ToolStarted("c1", "grep", "x")));
        Assert.Equal(("sub_agent", "finder", "tool_start"), ((string)nested["type"]!, (string)nested["agent"]!, (string)nested["event"]!["type"]!));

        var trimmed = JsonEvents.Map(new Trimmed(2, 900, 400));
        Assert.Equal(("context_reduced", "trimmed", 2), ((string)trimmed["type"]!, (string)trimmed["kind"]!, (int)trimmed["count"]!));
        Assert.Equal("loop_stopped", (string)JsonEvents.Map(new TurnEnded(TurnEnd.LoopStopped))["reason"]!);
    }

    [Fact]
    public void Renderer_WithoutStreaming_HidesTextButShowsTools()
    {
        var output = new StringWriter();
        var renderer = new Renderer(output, color: false, streamText: false);

        renderer.Render(new TextDelta("hidden"));
        renderer.Render(new ToolStarted("c", "grep", "x"));

        Assert.Equal("  ↳ grep x\n", output.ToString().ReplaceLineEndings("\n"));
    }
}

public class OptionsTests
{
    static Options? Parse(params string[] args) => Options.Parse(args, TextWriter.Null, TextWriter.Null).Options;

    [Fact]
    public void Parse_ReadsModesAndValues()
    {
        Assert.Equal(new Options("grok-4.5", true, true, "2026", true, "hi", true), Parse("-m", "grok-4.5", "--yolo", "-r", "2026", "-p", "hi", "--json"));
        Assert.Equal(new Options(null, false, true, null, true, null, false), Parse("--resume", "-p"));
        Assert.Equal(new Options(null, false, false, null, true, null, true), Parse("-p", "--json"));
        Assert.Equal(new Options(null, true, false, null, true, "later prompt", false), Parse("-p", "--yolo", "later prompt"));
    }

    [Fact]
    public void Parse_UnknownArgumentsAndHelp_ReturnExitCodes()
    {
        var error = new StringWriter();
        Assert.Equal((null, 2), Options.Parse(["--bogus"], TextWriter.Null, error));
        Assert.Contains("--bogus", error.ToString());
        Assert.Equal((null, 2), Options.Parse(["-p", "one", "two"], TextWriter.Null, TextWriter.Null));
        Assert.Equal((null, 2), Options.Parse(["no -p"], TextWriter.Null, error));
        Assert.Contains("needs -p", error.ToString());
        Assert.Equal((null, 2), Options.Parse(["--until", "make test"], TextWriter.Null, TextWriter.Null));
        Assert.Equal("make test", Parse("-p", "hi", "--until", "make test")?.Until);
        Assert.Equal((null, 2), Options.Parse(["--timeout", "30"], TextWriter.Null, TextWriter.Null));
        Assert.Equal((null, 2), Options.Parse(["-p", "hi", "--max-rounds", "0"], TextWriter.Null, TextWriter.Null));
        var limited = Parse("-p", "hi", "--allow", "dotnet", "--allow", "edits", "--max-rounds", "20", "--timeout", "600");
        Assert.Equal(["dotnet", "edits"], limited?.Allow);
        Assert.Equal((20, 600), (limited?.MaxRounds, limited?.Timeout));

        var output = new StringWriter();
        Assert.Equal((null, 0), Options.Parse(["--help"], output, TextWriter.Null));
        Assert.Contains("-p, --print", output.ToString());
    }
}
