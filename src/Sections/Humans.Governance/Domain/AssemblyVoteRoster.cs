using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// One person's entitlement to vote, frozen at open. The roster is the legal record of who
/// was entitled to vote and the denominator for turnout.
/// </summary>
/// <remarks>
/// Written exactly once, when the vote opens, and never recomputed: members approved,
/// expired, suspended or erased during the vote keep their row. The only later write is
/// Art. 17 erasure, which nulls <see cref="UserId"/> and leaves the row as a tombstone so
/// counts and the stored result stay valid.
/// </remarks>
internal sealed class AssemblyVoteRoster
{
    /// <summary>Unique identifier for the roster row.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning vote.</summary>
    public Guid VoteId { get; init; }

    /// <summary>Navigation to the owning vote.</summary>
    public AssemblyVote Vote { get; set; } = null!;

    /// <summary>
    /// The member. Bare cross-section reference — no nav, no FK constraint. Null after
    /// Art. 17 erasure, which keeps the row as an anonymous tombstone.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>The member's profile tier at open.</summary>
    public MembershipTier Tier { get; init; }

    /// <summary>Whether the member held the Board role at open.</summary>
    public bool IsBoardMember { get; init; }

    /// <summary>
    /// Whether this ballot counts in the official result. True for Asociados and Board
    /// members; false for the indicative audience.
    /// </summary>
    public bool IsOfficial { get; init; }

    /// <summary>When the vote-opened email was queued for this member.</summary>
    public Instant? NotifiedAt { get; set; }

    /// <summary>
    /// When the T-24h reminder was queued. The idempotency anchor: a stamped row is
    /// never reminded again, however often the hourly job runs.
    /// </summary>
    public Instant? ReminderSentAt { get; set; }
}
