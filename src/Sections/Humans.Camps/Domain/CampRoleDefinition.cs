using Humans.Base.Attributes;
using NodaTime;

namespace Humans.Camps.Domain;

/// <summary>Global: one definition applies across every camp-season.</summary>
internal sealed class CampRoleDefinition
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The per-role component of the Google Group key (<c>barrios-{year}-{slug}@{domain}</c>). Empty until the role
    /// needs a group, so several rows may share the empty slug.
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
    /// System-managed rows (Camp Lead, Workshop Lead) carry extra authorization semantics and are immutable except
    /// for <see cref="SlotCount"/> and <see cref="Description"/>, enforced in <c>CampRoleService</c>.
    /// </summary>
    public CampSpecialRole SpecialRole { get; set; } = CampSpecialRole.None;

    public ICollection<CampRoleAssignment> Assignments { get; } = new List<CampRoleAssignment>();

    public bool IsActive => DeactivatedAt is null;
}
