using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentAbuseDetector : IAgentAbuseDetector
{
    // Conservative, language-aware keyword list. False positives route to the standard
    // refusal reply ("This isn't something I can help with — please contact a coordinator")
    // rather than blocking, so precision matters more than recall.
    private static readonly string[] SelfHarmSignals =
    [
        "hurt myself", "kill myself", "suicide", "end my life",
        "hacerme daño", "matarme", "suicidio",
        "me faire du mal", "suicide",
        "mir etwas antun", "selbstmord",
        "farmi del male", "suicidio"
    ];

    public bool IsFlagged(string message, out string? reason)
    {
        var normalized = message.ToLowerInvariant();
        foreach (var signal in SelfHarmSignals)
        {
            if (normalized.Contains(signal, StringComparison.Ordinal))
            {
                reason = "self_harm_signal";
                return true;
            }
        }
        reason = null;
        return false;
    }
}
