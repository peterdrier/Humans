using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Store.Dtos;

public record OrderDto(
    Guid Id,
    Guid? CampSeasonId,
    Guid? TeamId,
    StoreOrderCounterpartyType CounterpartyType,
    string CounterpartyDisplayName,
    int Year,
    string? Label,
    StoreOrderState State,
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
public record OrderPaymentDto(
    decimal AmountEur,
    StorePaymentMethod Method,
    StorePaymentStatus Status,
    string? StripePaymentIntentId,
    string? ExternalRef,
    Instant ReceivedAt,
    string? Notes);
