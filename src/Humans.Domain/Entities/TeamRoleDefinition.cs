using NodaTime;
using Humans.Domain.Attributes;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Defines a named role on a team with a configurable number of slots.
/// </summary>
public class TeamRoleDefinition
{
    /// <summary>
    /// Unique identifier for the role definition.
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
    /// Name of the role (e.g. "Coordinator", "Secretary").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the role's responsibilities.
    /// </summary>
    [MarkdownContent]
    public string? Description { get; set; }

    /// <summary>
    /// Number of slots available for this role.
    /// </summary>
    public int SlotCount { get; set; } = 1;

    /// <summary>
    /// Priority levels for each slot, ordered by slot index.
    /// </summary>
    public List<SlotPriority> Priorities { get; set; } = [];

    /// <summary>
    /// Display order of this role within the team.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// When this role definition was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this role definition was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Period tag indicating when this role is active.
    /// Used for roster page filtering.
    /// </summary>
    public RolePeriod Period { get; set; } = RolePeriod.YearRound;

    /// <summary>
    /// Whether this role is visible on public/volunteer-facing views.
    /// When false, the role is hidden from volunteer-facing views but visible to coordinators and admins.
    /// </summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>
    /// Whether this role is the team's management/coordination role.
    /// At most one role per team can have this set to true.
    /// Assigning a member to this role automatically sets their TeamMemberRole to Coordinator.
    /// </summary>
    public bool IsManagement { get; set; }

    /// <summary>
    /// Navigation property to role slot assignments.
    /// </summary>
    public ICollection<TeamRoleAssignment> Assignments { get; } = new List<TeamRoleAssignment>();
}
