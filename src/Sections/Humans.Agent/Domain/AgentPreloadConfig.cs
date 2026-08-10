namespace Humans.Agent.Domain;

/// <summary>
/// Which preload bundle to include in the cacheable prompt prefix.
/// Tier1 is safe on Anthropic Tier-1 ITPM; Tier2 expands to the full
/// section-invariants corpus once the org has been promoted.
/// </summary>
internal enum AgentPreloadConfig
{
    Tier1 = 0,
    Tier2 = 1
}
