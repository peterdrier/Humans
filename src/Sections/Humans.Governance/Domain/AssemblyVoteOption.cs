namespace Humans.Governance.Domain;

/// <summary>
/// One authored option on a RankedChoice assembly vote. Aggregate-local to
/// <see cref="AssemblyVote"/> (cascade delete); YesNo votes have none.
/// </summary>
internal sealed class AssemblyVoteOption
{
    public Guid Id { get; init; }

    public Guid VoteId { get; init; }

    public AssemblyVote Vote { get; set; } = null!;

    /// <summary>
    /// Authored order. Disclosed on the results page as the final instant-runoff
    /// elimination tie-break, so it is part of the published method, not a display detail.
    /// </summary>
    public int Order { get; set; }

    /// <summary>Stable key recorded in ballots; never renumbered once Open.</summary>
    public string Key { get; set; } = string.Empty;

    public GovernanceLocalizedText Label { get; set; } = GovernanceLocalizedText.Empty;
}
