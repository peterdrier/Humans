using NodaTime;

namespace Humans.Application.Services.Store.Dtos;

public record ProductDto(
    Guid Id,
    int Year,
    string Name,
    string Description,
    decimal UnitPriceEur,
    decimal VatRatePercent,
    decimal? DepositAmountEur,
    LocalDate OrderableUntil,
    bool IsActive)
{
    /// <summary>
    /// Unit price including VAT, for display. Rounded to 2 dp away-from-zero to match the
    /// authoritative VAT rounding used by BalanceCalculator.
    /// </summary>
    public decimal UnitPriceInclVatEur => Math.Round(UnitPriceEur * (1 + VatRatePercent / 100m), 2, MidpointRounding.AwayFromZero);
}
