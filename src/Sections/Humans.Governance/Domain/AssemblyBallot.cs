using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// A roster member's current standing ballot — one per roster row, changeable until the
/// vote closes.
/// </summary>
/// <remarks>
/// The ballot content lives here and in <see cref="AssemblyBallotHistory"/> and nowhere
/// else: the audit entry for a cast or a change names the voter and the vote but never the
/// choice, so the audit log can be read freely without breaking the embargo.
/// </remarks>
internal sealed class AssemblyBallot
{
    /// <summary>Unique identifier for the ballot.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning vote.</summary>
    public Guid VoteId { get; init; }

    /// <summary>Navigation to the owning vote.</summary>
    public AssemblyVote Vote { get; set; } = null!;

    /// <summary>
    /// The roster row this ballot belongs to. Intra-section FK: the ballot is tied to the
    /// entitlement, not to the user directly, so erasure can anonymize the roster row and
    /// leave the ballot standing as part of the association's record.
    /// </summary>
    public Guid RosterId { get; init; }

    /// <summary>Navigation to the roster row.</summary>
    public AssemblyVoteRoster Roster { get; set; } = null!;

    /// <summary>
    /// The recorded answer. <see cref="AssemblyBallotChoice.Ranked"/> on a RankedChoice
    /// vote, where the preference order is in <see cref="Ranking"/>.
    /// </summary>
    public AssemblyBallotChoice Choice { get; set; }

    /// <summary>
    /// Ordered option keys, best first; RankedChoice only. A partial ranking is valid —
    /// unranked options are simply never preferred.
    /// </summary>
    public IReadOnlyList<string>? Ranking { get; set; }

    /// <summary>1 on the first cast, incremented on every change.</summary>
    public int Revision { get; set; }

    /// <summary>When the member first cast a ballot on this vote.</summary>
    public Instant CastAt { get; init; }

    /// <summary>When the member last changed it; equals <see cref="CastAt"/> at revision 1.</summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>Append-only revision history. Aggregate-local navigation.</summary>
    public ICollection<AssemblyBallotHistory> History { get; } = [];
}
