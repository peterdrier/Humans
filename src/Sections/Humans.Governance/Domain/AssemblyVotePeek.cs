using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// A record that an Admin looked at the live tally of an open vote. Append-only, and
/// listed on the results page after close.
/// </summary>
/// <remarks>
/// The embargo has exactly one hole, and this table is what makes it honest: a peek is
/// written in the same unit of work as the read it permits, so members can always see
/// whether anyone looked early, and who.
/// </remarks>
internal sealed class AssemblyVotePeek
{
    /// <summary>Unique identifier for the peek row.</summary>
    public Guid Id { get; init; }

    /// <summary>The vote that was peeked at.</summary>
    public Guid VoteId { get; init; }

    /// <summary>Navigation to the vote.</summary>
    public AssemblyVote Vote { get; set; } = null!;

    /// <summary>
    /// The Admin who looked. Bare cross-section reference — no nav. Settable for exactly one
    /// reason: an account merge repoints it at the surviving account, since it is the same
    /// human and the published peek list must keep naming them rather than a tombstone.
    /// The row is append-only in every other respect.
    /// </summary>
    public Guid AdminUserId { get; set; }

    /// <summary>When they looked.</summary>
    public Instant PeekedAt { get; init; }
}
