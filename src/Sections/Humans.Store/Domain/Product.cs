using Humans.Base.Attributes;
using NodaTime;

namespace Humans.Store.Domain;

internal sealed class Product
{
    public Guid Id { get; set; }
    public int Year { get; set; }
    public string Name { get; set; } = string.Empty;
    [MarkdownContent]
    public string Description { get; set; } = string.Empty;
    public decimal UnitPriceEur { get; set; }
    public decimal VatRatePercent { get; set; }
    public decimal? DepositAmountEur { get; set; }

    /// <summary>
    /// The 8-digit Holded revenue account (759000xx), from Acountax; several products may share one. Null until
    /// issued; invoicing a line with none is refused rather than booked to a catch-all.
    /// </summary>
    public int? HoldedRevenueAccountNum { get; set; }

    /// <summary>The 8-digit Holded expense account (6290xxxx) the item's gross spend books to.</summary>
    public int? HoldedExpenseAccountNum { get; set; }

    /// <summary>
    /// The 8-digit Holded internal-recharge account (6299xxxx, cost side), credited when a department's team order
    /// consumes the item. Team lines of a product without one are not booked.
    /// </summary>
    public int? HoldedRechargeAccountNum { get; set; }

    public LocalDate OrderableUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
}
