using NodaTime;

namespace Humans.Teams.Domain;

internal sealed class TeamRoleAssignment
{
    public Guid Id { get; init; }

    public Guid TeamRoleDefinitionId { get; init; }

    public TeamRoleDefinition TeamRoleDefinition { get; set; } = null!;

    public Guid TeamMemberId { get; init; }

    public TeamMember TeamMember { get; set; } = null!;

    /// <summary>Zero-based.</summary>
    public int SlotIndex { get; init; }

    public Instant AssignedAt { get; init; }

    public Guid AssignedByUserId { get; init; }
}
