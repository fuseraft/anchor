using System.Text;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

/// <summary>A running sub-agent at a glance, for a status line.</summary>
public sealed record SubAgentStats(string Id, int ToolCalls, long Tokens);

/// <summary>
/// Runs tasks in fresh agents with their own context, through the same gate. They run in the background of the turn
/// that started them: each one's final report is handed to <see cref="Report"/>, and the turn waits for them to finish.
/// </summary>
public sealed class SubAgentRunner(Toolbox toolbox, string systemPrompt, Action<AgentEvent> emit,
    Func<string?, Task<(IChatClient Client, ChatOptions Options)>> clientFor, Func<Compactor>? compactor = null, int? maxRounds = null) : IBackground
{
    public static readonly string[] ReadOnlyTools = ["read_file", "list_dir", "glob", "grep", "skill"];

    /// <summary>Tools a sub-agent never gets: it can't start sub-agents of its own or ask the user questions.</summary>
    public static readonly string[] MainAgentOnly = ["agent", "agent_status", "agent_stop", "ask_user"];

    public const int MaxRunning = 4;

    const int RecentActions = 3;

    const string Instructions = """


        # You are a sub-agent
        Another agent gave you the task below. Work on it with your tools, then reply with a concise final report.
        Only that final message reaches the other agent, so include every finding, path and detail it needs.
        You cannot ask questions; if something is ambiguous, make a reasonable choice and say so in the report.
        """;

    readonly Lock _lock = new();
    readonly List<Job> _jobs = [];
    int _started;

    /// <summary>Where finished sub-agents' reports go: the main agent, which reads them as notes.</summary>
    public Action<string>? Report { get; set; }

    /// <summary>Raised from the sub-agents' threads whenever <see cref="Running"/> may have changed.</summary>
    public event Action? Changed;

    /// <summary>The sub-agents running now, in the order they started.</summary>
    public IReadOnlyList<SubAgentStats> Running
    {
        get
        {
            lock (_lock)
                return [.. _jobs.Where(j => j.State == JobState.Running).Select(j => j.Stats())];
        }
    }

    public bool Busy
    {
        get { lock (_lock) return _jobs.Any(j => j.State == JobState.Running); }
    }

    /// <summary>Starts a sub-agent in the background and returns its id. Cancelling <paramref name="turn"/> stops it.</summary>
    public string Start(string task, AgentDefinition? definition, CancellationToken turn)
    {
        Job job;
        lock (_lock)
        {
            if (_jobs.Count(j => j.State == JobState.Running) >= MaxRunning)
                throw new ToolException($"{MaxRunning} sub-agents are already running. Wait for a report, or stop one with agent_stop.");
            job = new Job($"{definition?.Name ?? "agent"}-{++_started}", task, CancellationTokenSource.CreateLinkedTokenSource(turn));
            job.Done = RunInBackground(job, definition);
            _jobs.Add(job);
        }
        Changed?.Invoke();
        return job.Id;
    }

    Task RunInBackground(Job job, AgentDefinition? definition) => Task.Run(async () =>
        {
            SerialApprover.ActFor(job.Id);
            string report;
            try
            {
                report = await RunAsync(job.Assigned, definition, job.Stop.Token, job.Id, e =>
                {
                    if (job.Observe(e))
                        Changed?.Invoke();
                }, worker => job.Worker = worker);
            }
            catch (OperationCanceledException) when (job.Stop.IsCancellationRequested)
            {
                job.End(JobState.Stopped);
                Changed?.Invoke();
                return;
            }
            catch (Exception e)
            {
                report = $"[sub-agent {job.Id} failed: {e.Message}]";
            }
            // The report goes first, so the turn never sees this job idle with its report still missing.
            Report?.Invoke($"[anchor] Sub-agent {job.Id} finished. Its report:\n\n{report}");
            job.End(JobState.Finished);
            Changed?.Invoke();
        }, CancellationToken.None);

    /// <summary>What each sub-agent of this turn is doing, or has done; <paramref name="id"/> narrows it to one.</summary>
    public string Status(string? id = null)
    {
        List<Job> jobs;
        lock (_lock)
            jobs = id is null ? [.. _jobs] : [.. _jobs.Where(j => j.Id == id)];
        if (jobs.Count == 0)
            return id is null ? "No sub-agents have run in this turn." : throw new ToolException($"No sub-agent '{id}' in this turn.");
        return string.Join("\n\n", jobs.Select(j => j.Describe()));
    }

    /// <summary>Stops one running sub-agent; its report is never delivered.</summary>
    public async Task<string> StopAsync(string id)
    {
        Job? job;
        lock (_lock)
            job = _jobs.FirstOrDefault(j => j.Id == id);
        if (job is null)
            throw new ToolException($"No sub-agent '{id}' in this turn.");
        if (job.State != JobState.Running)
            return $"{id} is not running: it {job.State.ToString().ToLowerInvariant()}.";
        await job.Stop.CancelAsync();
        await job.Done;
        return $"Stopped {id}. Its report won't arrive.";
    }

    /// <summary>At the end of a turn: stops whatever still runs and forgets the turn's sub-agents.</summary>
    public async Task StopAsync()
    {
        List<Job> jobs;
        lock (_lock)
        {
            jobs = [.. _jobs];
            _jobs.Clear();
        }
        foreach (var job in jobs)
            await job.Stop.CancelAsync();
        await Task.WhenAll(jobs.Select(j => j.Done));
        foreach (var job in jobs)
            job.Stop.Dispose();
    }

    /// <summary>Runs one sub-agent to the end and returns its final report.</summary>
    public async Task<string> RunAsync(string task, AgentDefinition? definition, CancellationToken ct, string? label = null,
        Action<AgentEvent>? observe = null, Action<Agent>? started = null)
    {
        var name = label ?? definition?.Name ?? "agent";
        var tools = toolbox.Subset((definition?.Tools ?? ReadOnlyTools).Where(t => !MainAgentOnly.Contains(t)));
        var (client, options) = await clientFor(definition?.Model);
        var prompt = systemPrompt + Instructions + (definition is null ? "" : $"\n\n{definition.Prompt}");

        string? detail = null;
        var agent = new Agent(client, tools, prompt, e =>
        {
            if (e is TurnEnded ended)
                detail = ended.Detail;
            observe?.Invoke(e);
            emit(new SubAgentEvent(name, e));
        }, options, compactor: compactor?.Invoke())
        {
            MaxRounds = maxRounds,
        };
        started?.Invoke(agent);

        var end = await agent.RunTurnAsync(task, ct);
        ct.ThrowIfCancellationRequested();

        var report = agent.LastReply ?? "";
        return end == TurnEnd.Completed
            ? report.Length > 0 ? report : $"[sub-agent {name} finished without a report]"
            : $"[sub-agent {name} stopped early: {detail ?? end.ToString()}]\n\n{report}".TrimEnd();
    }

    enum JobState { Running, Finished, Stopped }

    sealed class Job(string id, string task, CancellationTokenSource stop)
    {
        readonly Lock _lock = new();
        readonly Queue<string> _recent = new();
        readonly DateTime _started = DateTime.UtcNow;
        DateTime? _ended;
        int _toolCalls;

        public string Id => id;

        public string Assigned => task;

        public CancellationTokenSource Stop => stop;

        public Task Done { get; set; } = Task.CompletedTask;

        public Agent? Worker { get; set; }

        volatile JobState _state;

        public JobState State => _state;

        /// <summary>Records a tool call; true when the stats changed.</summary>
        public bool Observe(AgentEvent e)
        {
            if (e is not ToolStarted t)
                return false;
            lock (_lock)
            {
                _toolCalls++;
                _recent.Enqueue($"{t.Name} {t.Summary}".TrimEnd());
                if (_recent.Count > RecentActions)
                    _recent.Dequeue();
            }
            return true;
        }

        public SubAgentStats Stats()
        {
            lock (_lock)
                return new(id, _toolCalls, Worker?.TurnTokens ?? 0);
        }

        public void End(JobState state)
        {
            lock (_lock)
                _ended = DateTime.UtcNow;
            _state = state;
        }

        public string Describe()
        {
            lock (_lock)
            {
                var elapsed = Duration((_ended ?? DateTime.UtcNow) - _started);
                var sb = new StringBuilder(State switch
                {
                    JobState.Running => $"{id}: running for {elapsed}",
                    JobState.Finished => $"{id}: finished after {elapsed}; its report has been delivered",
                    _ => $"{id}: stopped after {elapsed}",
                });
                sb.Append($", {_toolCalls} tool call{(_toolCalls == 1 ? "" : "s")}, {Worker?.TurnTokens ?? 0:N0} tokens");
                if (_recent.Count > 0)
                    sb.Append($"\nLatest: {string.Join("; ", _recent)}");
                return sb.Append($"\nTask: {(task.Length > 200 ? task[..197] + "..." : task)}").ToString();
            }
        }

        static string Duration(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{(int)t.TotalSeconds}s";
    }
}
