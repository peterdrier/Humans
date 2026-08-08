using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Assigns a team member to a specific slot in a role definition.
/// </summary>
public class TeamRoleAssignment
{
    /// <summary>
    /// Unique identifier for the role assignment.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the role definition.
    /// </summary>
    public Guid TeamRoleDefinitionId { get; init; }

    /// <summary>
    /// Navigation property to the role definition.
    /// </summary>
    public TeamRoleDefinition TeamRoleDefinition { get; set; } = null!;

    /// <summary>
    /// Foreign key to the team member.
    /// </summary>
    public Guid TeamMemberId { get; init; }

    /// <summary>
    /// Navigation property to the team member.
    /// </summary>
    public TeamMember TeamMember { get; set; } = null!;

    /// <summary>
    /// Zero-based index of the slot within the role definition.
    /// </summary>
    public int SlotIndex { get; init; }

    /// <summary>
    /// When this assignment was made.
    /// </summary>
    public Instant AssignedAt { get; init; }

    /// <summary>
    /// Foreign key to the user who made the assignment.
    /// </summary>
    public Guid AssignedByUserId { get; init; }
}
