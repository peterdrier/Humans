using NodaTime;
using Humans.Teams.Contracts;
namespace Humans.Teams.Domain;

internal sealed class TeamJoinRequest
{
    public Guid Id { get; init; }
    public Guid TeamId { get; init; }
    public Team Team { get; set; } = null!;
    public Guid UserId { get; init; }
    public TeamJoinRequestStatus Status { get; set; } = TeamJoinRequestStatus.Pending;
    public string? Message { get; set; }
    public Instant RequestedAt { get; init; }
    public Instant? ResolvedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewNotes { get; set; }
    public ICollection<TeamJoinRequestStateHistory> StateHistory { get; } = new List<TeamJoinRequestStateHistory>();

}
