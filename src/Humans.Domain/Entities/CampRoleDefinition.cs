using Humans.Domain.Attributes;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Defines a global role that can be assigned to humans within any camp-season.
/// CampAdmin-managed; the same definition applies across all camps.
/// </summary>
public class CampRoleDefinition
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Kebab-case identifier used in admin URLs and as the per-role component of the
    /// derived Google Group key (<c>barrios-{year}-{slug}@{domain}</c>).
    /// Unique case-insensitive; not user-visible.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    [MarkdownContent]
    public string? Description { get; set; }

    /// <summary>How many slot rows the camp Edit view renders per season. Service enforces as a soft cap on assignment.</summary>
    public int SlotCount { get; set; } = 1;

    /// <summary>How many slots must be filled for the camp to be compliant. 0 ≤ MinimumRequired ≤ SlotCount. 0 = role not tracked in the compliance report.</summary>
    public int MinimumRequired { get; set; } = 1;

    public int SortOrder { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    /// <summary>Soft-delete: deactivated roles preserve historical assignments but are hidden from new-assignment UI.</summary>
    public Instant? DeactivatedAt { get; set; }

    /// <summary>
    /// Marker for special, system-managed role definitions with extra authorization
    /// semantics (Camp Lead, Workshop Lead). <see cref="CampSpecialRole.None"/> is
    /// the default for regular admin-managed rows. Non-<c>None</c> rows are seeded
    /// by the CampAdmin "Seed system roles" admin button and are immutable except
    /// for <see cref="SlotCount"/> and <see cref="Description"/>. Enforced in
    /// <c>CampRoleService</c>.
    /// </summary>
    public CampSpecialRole SpecialRole { get; set; } = CampSpecialRole.None;

    public ICollection<CampRoleAssignment> Assignments { get; } = new List<CampRoleAssignment>();

    public bool IsActive => DeactivatedAt is null;
}
