namespace Anchor.Core;

/// <summary>Everything the core reports; renderers turn these into output.</summary>
public abstract record AgentEvent;

public sealed record TextDelta(string Text) : AgentEvent;

public sealed record ToolStarted(string CallId, string Name, string Summary) : AgentEvent;

public sealed record ToolFinished(string CallId, string Name, bool Ok, string Result) : AgentEvent;

public sealed record FileChanged(string Path, int Added, int Removed) : AgentEvent;

public sealed record LoopWarning(string Message) : AgentEvent;

public sealed record UsageReport(long Input, long Output, long CachedInput) : AgentEvent;

public sealed record TurnEnded(TurnEnd Reason, string? Detail = null) : AgentEvent;

public enum TurnEnd { Completed, Cancelled, LoopStopped, Error }
