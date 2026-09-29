using Anchor.Core;

namespace Anchor.Cli;

/// <summary>Token totals for the session, sub-agents included.</summary>
public sealed class SessionUsage
{
    public long Input { get; private set; }

    public long Output { get; private set; }

    public long Cached { get; private set; }

    public void Observe(AgentEvent e)
    {
        switch (e)
        {
            case UsageReport u:
                Input += u.Input;
                Output += u.Output;
                Cached += u.CachedInput;
                break;
            case SubAgentEvent s:
                Observe(s.Inner);
                break;
        }
    }
}
