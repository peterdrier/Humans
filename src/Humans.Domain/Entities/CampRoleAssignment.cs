using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// One human filling one role slot for one camp-season. Soft-cap on slots is
/// enforced by <c>ICampRoleService.AssignAsync</c>; the unique index on
/// <c>(CampSeasonId, CampRoleDefinitionId, CampMemberId)</c> guarantees a human
/// cannot hold the same role twice in the same season.
/// </summary>
public class CampRoleAssignment
{
    public Guid Id { get; init; }

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public Guid CampRoleDefinitionId { get; init; }
    public CampRoleDefinition Definition { get; set; } = null!;

    public Guid CampMemberId { get; init; }
    public CampMember CampMember { get; set; } = null!;

    public Instant AssignedAt { get; init; }

    public Guid AssignedByUserId { get; init; }
}
