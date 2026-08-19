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
    /// The 8-digit Holded chart-of-accounts number this item's external revenue books to
    /// (<c>75900001</c> Bus Tickets, <c>75900002</c> ice, …), set by StoreAdmin from Acountax's
    /// numbering. Null until Acountax has issued one — issuing an invoice for a line whose product
    /// has no number is refused rather than booked to a catch-all. The internal-recharge twin
    /// (<c>75910002</c>) is derived, never stored.
    /// </summary>
    public int? HoldedRevenueAccountNum { get; set; }

    public LocalDate OrderableUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
}
