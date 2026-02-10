using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Represents membership of a user in a team.
/// </summary>
public class TeamMember
{
    /// <summary>
    /// Unique identifier for the team membership.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the team.
    /// </summary>
    public Guid TeamId { get; init; }

    /// <summary>
    /// Navigation property to the team.
    /// </summary>
    public Team Team { get; set; } = null!;

    /// <summary>
    /// Foreign key to the user.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Role within the team.
    /// </summary>
    public TeamMemberRole Role { get; set; } = TeamMemberRole.Member;

    /// <summary>
    /// When the user joined this team.
    /// </summary>
    public Instant JoinedAt { get; init; }

    /// <summary>
    /// When the membership ended (null if still active).
    /// </summary>
    public Instant? LeftAt { get; set; }

    /// <summary>
    /// Whether this is currently an active membership.
    /// </summary>
    public bool IsActive => !LeftAt.HasValue;
}
