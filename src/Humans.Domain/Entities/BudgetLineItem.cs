using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Detail row within a budget category (e.g., "Food", "PA System Rental").
/// </summary>
public class BudgetLineItem
{
    public Guid Id { get; init; }
    public Guid BudgetCategoryId { get; init; }
    public BudgetCategory? BudgetCategory { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Guid? ResponsibleTeamId { get; set; }
    public Team? ResponsibleTeam { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Expected date for this expense, used for cashflow projections.
    /// </summary>
    public LocalDate? ExpectedDate { get; set; }

    /// <summary>
    /// VAT rate percentage (0, 10, or 21). Default: 0 (no VAT).
    /// </summary>
    public int VatRate { get; set; }

    public int SortOrder { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
