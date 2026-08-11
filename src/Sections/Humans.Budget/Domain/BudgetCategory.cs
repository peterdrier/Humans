using NodaTime;
using Humans.Budget.Contracts;

namespace Humans.Budget.Domain;

/// <summary>
/// Third-level budget container within a group (e.g., "Cantina", "Sound").
/// Holds the allocated budget amount.
/// </summary>
internal sealed class BudgetCategory
{
    public Guid Id { get; init; }
    public Guid BudgetGroupId { get; init; }
    public BudgetGroup? BudgetGroup { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal AllocatedAmount { get; set; }
    public ExpenditureType ExpenditureType { get; set; } = ExpenditureType.OpEx;
    public Guid? TeamId { get; set; }

    public int SortOrder { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public ICollection<BudgetLineItem> LineItems { get; } = new List<BudgetLineItem>();
}
