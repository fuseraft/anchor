using Anchor.Core;

namespace Anchor.Cli;

/// <summary>Token totals for the session, sub-agents included. Parallel sub-agents report from several threads.</summary>
public sealed class SessionUsage
{
    readonly Lock _lock = new();

    public long Input { get; private set; }

    public long Output { get; private set; }

    public long Cached { get; private set; }

    public void Observe(AgentEvent e)
    {
        switch (e)
        {
            case UsageReport u:
                lock (_lock)
                {
                    Input += u.Input;
                    Output += u.Output;
                    Cached += u.CachedInput;
                }
                break;
            case SubAgentEvent s:
                Observe(s.Inner);
                break;
        }
    }
}
