using Humans.Store.Domain;
using NodaTime;

namespace Humans.Store.Services.Dtos;

internal sealed record OrderDto(
    Guid Id,
    Guid? CampSeasonId,
    Guid? TeamId,
    OrderCounterpartyType CounterpartyType,
    string CounterpartyDisplayName,
    int Year,
    string? Label,
    OrderState State,
    string? CounterpartyName,
    string? CounterpartyVatId,
    string? CounterpartyAddress,
    string? CounterpartyCountryCode,
    string? CounterpartyEmail,
    Guid? IssuedInvoiceId,
    IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<OrderPaymentDto> Payments,
    decimal LinesSubtotalEur,
    decimal VatTotalEur,
    decimal DepositTotalEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur,
    Instant CreatedAt);

/// <summary>One recorded payment against a camp order (a row in <c>store_payments</c>).</summary>
internal sealed record OrderPaymentDto(
    decimal AmountEur,
    PaymentMethod Method,
    PaymentStatus Status,
    string? StripePaymentIntentId,
    string? ExternalRef,
    Instant ReceivedAt,
    string? Notes);
