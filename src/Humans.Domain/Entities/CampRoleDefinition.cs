using Humans.Domain.Attributes;
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

    public ICollection<CampRoleAssignment> Assignments { get; } = new List<CampRoleAssignment>();

    public bool IsActive => DeactivatedAt is null;
}
