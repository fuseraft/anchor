using Anchor.Core;

namespace Anchor.Cli;

/// <summary>
/// The REPL's status line: the model, how full the context is (colored by how near compaction is), the /until check,
/// a downloaded update waiting for a restart, and while work runs, what it is and the sub-agents running under it.
/// </summary>
public sealed class StatusLine(Agent agent, Renderer renderer, IReplScreen screen, long contextWindow, SubAgentRunner? subAgents,
    Func<string> model, Func<string?> until)
{
    // Whether work is running, and what it is when it isn't a turn; the agent's context changes and sub-agent changes
    // redraw it from their own threads while it does.
    volatile bool _working;
    volatile string? _doing;
    long _shownTokens;
    // Context at the start of the running turn, and how much the last turn added; they predict when compaction is near.
    long? _turnStart;
    long _lastGrowth;

    /// <summary>The context count last drawn, for redraws from threads where the history may be changing under an estimate.</summary>
    public long ShownTokens => Interlocked.Read(ref _shownTokens);

    /// <param name="doing">What's running when it isn't a turn, such as /compact; it leaves the turn's growth alone.</param>
    public void Set(bool working, string? doing = null)
    {
        var tokens = agent.ContextTokens;
        if (doing is null && working)
            _turnStart = tokens;
        else if (doing is null && _turnStart is { } start)
        {
            _lastGrowth = Math.Max(0, tokens - start);
            _turnStart = null;
        }
        (_working, _doing) = (working, doing);
        Draw(tokens, working, doing);
    }

    /// <summary>Draws the running status again with <paramref name="tokens"/> of context; it leaves the turn's growth alone.</summary>
    public void Redraw(long tokens)
    {
        if (_working)
            Draw(tokens, working: true, _doing);
    }

    /// <summary>Draws the status again as it stands, such as when an update has finished downloading.</summary>
    public void Refresh() => Draw(ShownTokens, _working, _doing);

    void Draw(long tokens, bool working, string? doing)
    {
        Interlocked.Exchange(ref _shownTokens, tokens);
        var used = $"{100.0 * tokens / contextWindow:0}% context";
        if (agent.Compactor is { } c)
            used = ContextLevelOf(tokens, _lastGrowth, c.TriggerTokens) switch
            {
                ContextLevel.Compacting => renderer.Error(used),
                ContextLevel.Near => renderer.Warning(used),
                _ => renderer.Success(used),
            };
        var status = $"{renderer.Accent(model())} · {used}" + (until() is { } check ? $" · until {check}" : "")
            + UpdateStatus(Update.Downloaded) + (doing is null ? "" : $" · {doing}");
        if (working && subAgents is not null)
            status += SubAgentStatus(subAgents.Running);
        screen.Status(status, working);
    }

    public enum ContextLevel { Clear, Near, Compacting }

    /// <summary>
    /// How close the context is to compaction, which starts past <paramref name="trigger"/> tokens: Compacting when
    /// it's already past, so the next request compacts first; Near when a turn that adds as much as the last one
    /// (<paramref name="lastGrowth"/>) would get there; Clear otherwise.
    /// </summary>
    public static ContextLevel ContextLevelOf(long tokens, long lastGrowth, long trigger) =>
        tokens > trigger ? ContextLevel.Compacting : tokens + lastGrowth > trigger ? ContextLevel.Near : ContextLevel.Clear;

    /// <summary>A downloaded update for the status line, such as " · ↑ 0.8.6 on restart"; empty when there's none.</summary>
    public static string UpdateStatus(string? version) => version is null ? "" : $" · ↑ {version} on restart";

    /// <summary>The running sub-agents for the status line, such as " · agent-1: 7 calls, 12k tokens"; empty when none run.</summary>
    public static string SubAgentStatus(IReadOnlyList<SubAgentStats> running) =>
        string.Concat(running.Select(s => $" · {s.Id}: {s.ToolCalls} call{(s.ToolCalls == 1 ? "" : "s")}, {Tokens(s.Tokens)} tokens"));

    static string Tokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1_000}k",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };
}
