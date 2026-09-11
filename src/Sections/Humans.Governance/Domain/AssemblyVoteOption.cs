namespace Humans.Governance.Domain;

/// <summary>
/// One authored option on a RankedChoice assembly vote. Aggregate-local to
/// <see cref="AssemblyVote"/> (cascade delete); YesNo votes have none.
/// </summary>
internal sealed class AssemblyVoteOption
{
    /// <summary>Unique identifier for the option.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning vote.</summary>
    public Guid VoteId { get; init; }

    /// <summary>Navigation to the owning vote.</summary>
    public AssemblyVote Vote { get; set; } = null!;

    /// <summary>
    /// Authored order. Disclosed on the results page as the final instant-runoff
    /// elimination tie-break, so it is part of the published method, not a display detail.
    /// </summary>
    public int Order { get; set; }

    /// <summary>Stable key recorded in ballots; never renumbered once Open.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Per-culture label.</summary>
    public GovernanceLocalizedText Label { get; set; } = GovernanceLocalizedText.Empty;
}
