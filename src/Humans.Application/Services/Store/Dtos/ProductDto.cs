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
    bool IsActive);
