using NodaTime;
using Humans.Teams.Contracts;
namespace Humans.Teams.Domain;

internal sealed class TeamMember
{
    public Guid Id { get; init; }

    public Guid TeamId { get; init; }

    public Team Team { get; set; } = null!;

    public Guid UserId { get; init; }

    public TeamMemberRole Role { get; set; } = TeamMemberRole.Member;

    public Instant JoinedAt { get; init; }

    /// <summary>Null while active.</summary>
    public Instant? LeftAt { get; set; }

    public bool IsActive => !LeftAt.HasValue;

    public ICollection<TeamRoleAssignment> RoleAssignments { get; } = new List<TeamRoleAssignment>();
}
