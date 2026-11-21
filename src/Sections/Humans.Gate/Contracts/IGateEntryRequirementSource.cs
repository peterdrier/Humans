namespace Humans.Gate.Contracts;

/// <summary>
/// "What must this person do before entry?" A section that knows of something the holder has to settle at the gate
/// (sign a paper agreement, show a document) answers it, so Gate never learns about consents or agreements itself.
/// The agent resolves each item on the spot and confirms it; the gate refuses the admit until every item is confirmed
/// and records them with it.
/// </summary>
public interface IGateEntryRequirementSource
{
    /// <summary>Asked on every scan of a ticket matched to <paramref name="userId"/>; must be fast and live.</summary>
    Task<IReadOnlyList<GateEntryRequirement>> GetOutstandingAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default);
}

/// <summary>One thing the holder must settle before entry.</summary>
/// <param name="Key">Stable per item and person, so a confirmation can be matched on the decision.</param>
/// <param name="Instruction">What the agent does, in plain words: shown on the card and stored with the admit.</param>
public sealed record GateEntryRequirement(string Key, string Instruction);
