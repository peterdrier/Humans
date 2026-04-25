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

    /// <summary>How many slot rows exist for this role per camp-season.</summary>
    public int SlotCount { get; set; } = 1;

    /// <summary>How many slots must be filled for the camp to be compliant. 0 ≤ MinimumRequired ≤ SlotCount.</summary>
    public int MinimumRequired { get; set; } = 1;

    public int SortOrder { get; set; }

    /// <summary>True if the compliance report should track this role.</summary>
    public bool IsRequired { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    /// <summary>Soft-delete: deactivated roles preserve historical assignments but are hidden from new assignment UI.</summary>
    public Instant? DeactivatedAt { get; set; }

    public ICollection<CampRoleAssignment> Assignments { get; } = new List<CampRoleAssignment>();
}
