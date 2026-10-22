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
    /// The 8-digit Holded revenue account, from Acountax. Null until issued; invoicing a line with none is refused
    /// rather than booked to a catch-all. The internal-recharge twin is derived, never stored.
    /// </summary>
    public int? HoldedRevenueAccountNum { get; set; }

    public LocalDate OrderableUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
}
