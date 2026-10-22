namespace Humans.Agent.Domain;

/// <summary>
/// Tier1 fits Anthropic's Tier-1 ITPM limit; Tier2 adds the full section-invariants corpus once the org is promoted.
/// </summary>
internal enum AgentPreloadConfig
{
    Tier1 = 0,
    Tier2 = 1
}
