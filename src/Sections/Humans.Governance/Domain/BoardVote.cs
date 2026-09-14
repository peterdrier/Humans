using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// An individual Board member's vote on a tier application.
/// Transient working data — records are deleted when the application is finalized (GDPR data minimization).
/// Only the collective decision (Application.DecisionNote, BoardMeetingDate) is retained.
/// </summary>
internal sealed class BoardVote
{
    public Guid Id { get; init; }

    public Guid ApplicationId { get; init; }

    public Application Application { get; set; } = null!;

    /// <summary>
    /// Foreign key to the Board member who cast the vote. Use
    /// <c>IUserService</c> to hydrate display info — cross-domain
    /// navigation properties are forbidden on this entity (design-rules §6).
    /// </summary>
    public Guid BoardMemberUserId { get; init; }

    public VoteChoice Vote { get; set; }

    public string? Note { get; set; }

    public Instant VotedAt { get; init; }

    public Instant? UpdatedAt { get; set; }
}
