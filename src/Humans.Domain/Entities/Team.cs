using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Represents a working group or team within the organization.
/// </summary>
public class Team
{
    /// <summary>
    /// Unique identifier for the team.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Team name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Team description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// URL-friendly slug for the team.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Whether the team is currently active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether joining this team requires approval from a metalead or board member.
    /// </summary>
    public bool RequiresApproval { get; set; } = true;

    /// <summary>
    /// Identifies system-managed teams with automatic membership sync.
    /// </summary>
    public SystemTeamType SystemTeamType { get; set; } = SystemTeamType.None;

    /// <summary>
    /// When the team was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the team was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to team members.
    /// </summary>
    public ICollection<TeamMember> Members { get; } = new List<TeamMember>();

    /// <summary>
    /// Navigation property to join requests.
    /// </summary>
    public ICollection<TeamJoinRequest> JoinRequests { get; } = new List<TeamJoinRequest>();

    /// <summary>
    /// Navigation property to associated Google resources.
    /// </summary>
    public ICollection<GoogleResource> GoogleResources { get; } = new List<GoogleResource>();

    /// <summary>
    /// Whether this is a system-managed team.
    /// </summary>
    public bool IsSystemTeam => SystemTeamType != SystemTeamType.None;
}
