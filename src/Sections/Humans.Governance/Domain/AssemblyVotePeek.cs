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
    public Guid Id { get; init; }

    public Guid VoteId { get; init; }

    public AssemblyVote Vote { get; set; } = null!;

    /// <summary>
    /// Settable only so an account merge can repoint it at the surviving account: the published
    /// peek list must keep naming the human, not a tombstone. Append-only in every other respect.
    /// </summary>
    public Guid AdminUserId { get; set; }

    public Instant PeekedAt { get; init; }
}
