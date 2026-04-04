using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Top-level budget container for a fiscal year.
/// </summary>
public class BudgetYear
{
    public Guid Id { get; init; }
    public string Year { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public BudgetYearStatus Status { get; set; } = BudgetYearStatus.Draft;
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Instant? DeletedAt { get; set; }
    public ICollection<BudgetGroup> Groups { get; } = new List<BudgetGroup>();
    public ICollection<BudgetAuditLog> AuditLogs { get; } = new List<BudgetAuditLog>();
}
