using NodaTime;

namespace Profiles.Domain.Entities;

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
    /// Navigation property to associated Google resources.
    /// </summary>
    public ICollection<GoogleResource> GoogleResources { get; } = new List<GoogleResource>();
}
