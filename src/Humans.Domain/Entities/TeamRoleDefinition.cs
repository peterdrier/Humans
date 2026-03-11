using NodaTime;
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
    /// Name of the role (e.g. "Lead", "Secretary").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the role's responsibilities.
    /// </summary>
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
    /// Whether this role is the team lead role.
    /// </summary>
    public bool IsLeadRole => string.Equals(Name, "Lead", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Navigation property to role slot assignments.
    /// </summary>
    public ICollection<TeamRoleAssignment> Assignments { get; } = new List<TeamRoleAssignment>();
}
