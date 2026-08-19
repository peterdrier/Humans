
namespace Humans.Store.Services;

internal sealed record MutationResult(bool Succeeded, string? ErrorMessage)
{
    public static MutationResult Success { get; } = new(true, null);

    public static MutationResult Failure(string message) => new(false, message);
}

internal sealed record ProductSaveRequest(
    Guid? Id,
    int Year,
    string? Name,
    string? Description,
    decimal UnitPriceEur,
    decimal VatRatePercent,
    decimal? DepositAmountEur,
    string? OrderableUntil,
    bool IsActive,
    int? HoldedRevenueAccountNum);

internal sealed record CatalogSaveResult(
    bool Succeeded,
    bool Created,
    string? ErrorField,
    string? ErrorMessage)
{
    public static CatalogSaveResult Success(bool created) => new(true, created, null, null);

    public static CatalogSaveResult Failure(string? field, string message) => new(false, false, field, message);
}
