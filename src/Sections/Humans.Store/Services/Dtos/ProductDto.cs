using NodaTime;

namespace Humans.Store.Services.Dtos;

internal sealed record ProductDto(
    Guid Id,
    int Year,
    string Name,
    string Description,
    decimal UnitPriceEur,
    decimal VatRatePercent,
    decimal? DepositAmountEur,
    LocalDate OrderableUntil,
    bool IsActive,
    int? HoldedRevenueAccountNum = null)
{
    /// <summary>
    /// Distance from an item's external revenue account to its internal-recharge twin:
    /// <c>75900002</c> (ice sold to camps) → <c>75910002</c> (ice consumed by a department).
    /// Internal consumption must not land in the external account — it would gross up declared
    /// revenue for an event that carries no VAT and no counterparty.
    /// </summary>
    private const int InternalRechargeAccountOffset = 10_000;

    /// <summary>
    /// Unit price including VAT, for display. Rounded to 2 dp away-from-zero to match the
    /// authoritative VAT rounding used by BalanceCalculator.
    /// </summary>
    public decimal UnitPriceInclVatEur => Math.Round(UnitPriceEur * (1 + VatRatePercent / 100m), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The account season-close department-allocation journal entries credit at catalog price.
    /// Derived, never stored; Acountax creates it in Holded the first time a department consumes
    /// the item.
    /// </summary>
    public int? InternalRechargeAccountNum =>
        HoldedRevenueAccountNum is { } num ? num + InternalRechargeAccountOffset : null;
}
