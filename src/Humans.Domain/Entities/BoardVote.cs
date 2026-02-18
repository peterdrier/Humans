using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// An individual Board member's vote on a tier application.
/// Transient working data — records are deleted when the application is finalized (GDPR data minimization).
/// Only the collective decision (Application.DecisionNote, BoardMeetingDate) is retained.
/// </summary>
public class BoardVote
{
    /// <summary>
    /// Unique identifier for the vote.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the application being voted on.
    /// </summary>
    public Guid ApplicationId { get; init; }

    /// <summary>
    /// Navigation property to the application.
    /// </summary>
    public Application Application { get; set; } = null!;

    /// <summary>
    /// Foreign key to the Board member who cast the vote.
    /// </summary>
    public Guid BoardMemberUserId { get; init; }

    /// <summary>
    /// Navigation property to the Board member.
    /// </summary>
    public User BoardMemberUser { get; set; } = null!;

    /// <summary>
    /// The vote choice.
    /// </summary>
    public VoteChoice Vote { get; set; }

    /// <summary>
    /// Optional note explaining the vote.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// When the vote was first cast.
    /// </summary>
    public Instant VotedAt { get; init; }

    /// <summary>
    /// When the vote was last updated (null if never changed).
    /// </summary>
    public Instant? UpdatedAt { get; set; }
}
