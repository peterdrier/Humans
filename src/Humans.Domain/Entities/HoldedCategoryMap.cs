using NodaTime;
namespace Humans.Domain.Entities;

/// <summary>Maps a BudgetCategory to its dedicated Holded account + fallback tag.</summary>
public class HoldedCategoryMap
{
    public Guid Id { get; init; }
    public Guid BudgetCategoryId { get; set; }     // FK-only, no nav (cross-section)
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public string Tag { get; set; } = "";          // dash-free normalized fallback key
    public bool IsActive { get; set; } = true;
    public Instant? ArchivedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
