using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Second-level budget container within a year (e.g., "Departments", "Site Infrastructure").
/// </summary>
public class BudgetGroup
{
    public Guid Id { get; init; }
    public Guid BudgetYearId { get; init; }
    public BudgetYear? BudgetYear { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsRestricted { get; set; }
    public bool IsDepartmentGroup { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public ICollection<BudgetCategory> Categories { get; } = new List<BudgetCategory>();
}
