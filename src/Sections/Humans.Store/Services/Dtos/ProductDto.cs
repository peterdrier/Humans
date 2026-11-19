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
    int? HoldedRevenueAccountNum = null,
    int? HoldedExpenseAccountNum = null,
    int? HoldedRechargeAccountNum = null)
{
    /// <summary>Display only, rounded like BalanceCalculator.</summary>
    public decimal UnitPriceInclVatEur => Math.Round(UnitPriceEur * (1 + VatRatePercent / 100m), 2, MidpointRounding.AwayFromZero);
}
