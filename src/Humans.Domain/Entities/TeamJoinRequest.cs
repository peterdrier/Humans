using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Represents a request to join a team.
/// </summary>
public class TeamJoinRequest
{
    public Guid Id { get; init; }
    public Guid TeamId { get; init; }
    public Team Team { get; set; } = null!;
    public Guid UserId { get; init; }
    public User User { get; set; } = null!;
    public TeamJoinRequestStatus Status { get; set; } = TeamJoinRequestStatus.Pending;
    public string? Message { get; set; }
    public Instant RequestedAt { get; init; }
    public Instant? ResolvedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public string? ReviewNotes { get; set; }
    public ICollection<TeamJoinRequestStateHistory> StateHistory { get; } = new List<TeamJoinRequestStateHistory>();

    public void Approve(Guid reviewerUserId, string? notes, IClock clock)
    {
        if (Status != TeamJoinRequestStatus.Pending)
            throw new InvalidOperationException("Can only approve pending requests");

        Status = TeamJoinRequestStatus.Approved;
        ReviewedByUserId = reviewerUserId;
        ReviewNotes = notes;
        ResolvedAt = clock.GetCurrentInstant();
    }

    public void Reject(Guid reviewerUserId, string reason, IClock clock)
    {
        if (Status != TeamJoinRequestStatus.Pending)
            throw new InvalidOperationException("Can only reject pending requests");

        Status = TeamJoinRequestStatus.Rejected;
        ReviewedByUserId = reviewerUserId;
        ReviewNotes = reason;
        ResolvedAt = clock.GetCurrentInstant();
    }

    public void Withdraw(IClock clock)
    {
        if (Status != TeamJoinRequestStatus.Pending)
            throw new InvalidOperationException("Can only withdraw pending requests");

        Status = TeamJoinRequestStatus.Withdrawn;
        ResolvedAt = clock.GetCurrentInstant();
    }
}
