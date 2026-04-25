using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Assigns a CampMember to a specific slot of a CampRoleDefinition for a single CampSeason.
/// Slot uniqueness is enforced via composite unique index in EF config.
/// </summary>
public class CampRoleAssignment
{
    public Guid Id { get; init; }

    public Guid CampRoleDefinitionId { get; init; }
    public CampRoleDefinition CampRoleDefinition { get; set; } = null!;

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public Guid CampMemberId { get; init; }
    public CampMember CampMember { get; set; } = null!;

    /// <summary>Zero-based slot index, must be &lt; SlotCount.</summary>
    public int SlotIndex { get; init; }

    public Instant AssignedAt { get; init; }

    public Guid AssignedByUserId { get; init; }
    public User AssignedByUser { get; set; } = null!;
}
