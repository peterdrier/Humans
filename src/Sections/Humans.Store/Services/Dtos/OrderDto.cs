using Humans.Store.Contracts;
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
    Instant CreatedAt)
{
    /// <summary>The state half of <c>DeleteOrderAsync</c>'s guard; authorization is separate.</summary>
    public bool IsDeletable => State == OrderState.Open && Payments.Count == 0 && BalanceEur == 0m;

    /// <summary>The state half of issuance; the issuer's zero-balance and Holded checks still refuse authoritatively.</summary>
    public bool IsInvoiceIssuable => State == OrderState.Open && Lines.Count > 0;
}

internal sealed record OrderPaymentDto(
    Guid Id,
    decimal AmountEur,
    PaymentMethod Method,
    PaymentStatus Status,
    string? StripePaymentIntentId,
    string? ExternalRef,
    Instant ReceivedAt,
    string? Notes);
