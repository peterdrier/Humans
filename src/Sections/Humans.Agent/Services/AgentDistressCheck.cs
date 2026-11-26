namespace Humans.Agent.Services;

internal sealed class AgentDistressCheck : IAgentDistressCheck
{
    // False positives get the fixed distress reply rather than a block, so precision matters more than recall.
    private static readonly string[] SelfHarmSignals =
    [
        "hurt myself", "kill myself", "suicide", "end my life",
        "hacerme daño", "matarme", "suicidio",
        "me faire du mal", "suicide",
        "mir etwas antun", "selbstmord",
        "farmi del male", "suicidio"
    ];

    public bool Matches(string question)
    {
        var normalized = question.ToLowerInvariant();
        return SelfHarmSignals.Any(signal => normalized.Contains(signal, StringComparison.Ordinal));
    }
}
