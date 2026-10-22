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
    /// <c>75900002</c> (ice sold to camps) → <c>75910002</c> (ice consumed by a department). Internal consumption in the
    /// external account would gross up declared revenue for an event with no VAT and no counterparty.
    /// </summary>
    private const int InternalRechargeAccountOffset = 10_000;

    /// <summary>Display only, rounded like BalanceCalculator.</summary>
    public decimal UnitPriceInclVatEur => Math.Round(UnitPriceEur * (1 + VatRatePercent / 100m), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Credited by season-close department-allocation journal entries. Acountax creates it in Holded the first time a
    /// department consumes the item.
    /// </summary>
    public int? InternalRechargeAccountNum =>
        HoldedRevenueAccountNum is { } num ? num + InternalRechargeAccountOffset : null;
}
