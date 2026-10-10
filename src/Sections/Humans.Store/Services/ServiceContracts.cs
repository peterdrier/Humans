
using Humans.Store.Domain;
using NodaTime;

namespace Humans.Store.Services;

internal sealed record MutationResult(bool Succeeded, string? ErrorKey, Guid? CreatedId = null)
{
    public static MutationResult Success { get; } = new(true, null);

    public static MutationResult Failure(string errorKey) => new(false, errorKey);
}

internal sealed record AdminMutationResult(bool Succeeded, string? Refusal)
{
    public static AdminMutationResult Success { get; } = new(true, null);
    public static AdminMutationResult Refused(string reason) => new(false, reason);
}

internal sealed record CheckoutSessionResult(string? SessionUrl, string? ErrorKey, decimal? MaximumAmount = null);

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
    string? ErrorMessage,
    Guid? CreatedId = null)
{
    public static CatalogSaveResult Success(bool created) => new(true, created, null, null);

    public static CatalogSaveResult Failure(string? field, string message) => new(false, false, field, message);
}

internal sealed record OrderYearRepairRow(
    Guid OrderId,
    Guid? CampSeasonId,
    string? CampName,
    int? ResolvedYear);

internal sealed record PaymentMethodRepairRow(
    Guid PaymentId,
    Guid OrderId,
    PaymentMethod Method,
    decimal AmountEur,
    Instant ReceivedAt);

internal sealed record OrderYearRepairReport(IReadOnlyList<OrderYearRepairRow> Rows)
{
    public int ResolvableCount => Rows.Count(row => row.ResolvedYear.HasValue);
}
