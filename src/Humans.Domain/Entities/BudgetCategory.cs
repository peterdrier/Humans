using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Third-level budget container within a group (e.g., "Cantina", "Sound").
/// Holds the allocated budget amount.
/// </summary>
public class BudgetCategory
{
    public Guid Id { get; init; }
    public Guid BudgetGroupId { get; init; }
    public BudgetGroup? BudgetGroup { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public decimal AllocatedAmount { get; set; }
    public ExpenditureType ExpenditureType { get; set; } = ExpenditureType.OpEx;
    public Guid? TeamId { get; set; }

    /// <summary>
    /// Cross-domain navigation to the owning team. Do not use — callers resolve
    /// team names via <see cref="Humans.Application.Interfaces.ITeamService"/>,
    /// keyed off <see cref="TeamId"/>. Retained only so EF's configured
    /// relationship keeps the FK constraint.
    /// </summary>
    [Obsolete("Cross-domain nav. Use TeamId + ITeamService to resolve the team.")]
    public Team? Team { get; set; }

    public int SortOrder { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public ICollection<BudgetLineItem> LineItems { get; } = new List<BudgetLineItem>();
}
