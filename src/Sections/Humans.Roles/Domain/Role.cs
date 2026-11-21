using Humans.Base.Attributes;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Roles.Domain;

internal sealed class Role
{
    public Guid Id { get; init; }

    /// <summary>A kind some section registers as an owner kind, such as <c>Team</c>.</summary>
    public string OwnerKind { get; init; } = string.Empty;

    public Guid OwnerId { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Made from the name, unique per owner.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Optional second link key, unique per owner across both slugs.</summary>
    public string? CustomSlug { get; set; }

    [MarkdownContent]
    public string? Description { get; set; }

    public int SlotCount { get; set; } = 1;

    /// <summary>One per slot, by slot number.</summary>
    public List<SlotPriority> Priorities { get; set; } = [];

    public int SortOrder { get; set; }

    public RolePeriod Period { get; set; } = RolePeriod.YearRound;

    /// <summary>Whole hours per year. Informational only: gates nothing.</summary>
    public int? EstimatedHours { get; set; }

    /// <summary>When false, only people who manage the owner's roles, and Board, see it.</summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>At most one per owner. Its holders hold every permission that applies to the owner.</summary>
    public bool IsCoordinator { get; set; }

    /// <summary>Permission keys held by this role's holders. Empty on the coordinator role.</summary>
    public List<string> PermissionKeys { get; set; } = [];

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public ICollection<RoleAssignment> Assignments { get; } = new List<RoleAssignment>();
}
