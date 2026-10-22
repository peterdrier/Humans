using NodaTime;

namespace Humans.Store.Contracts;

/// <summary>
/// Priced the way the admin summary prices it. <see cref="LineGross"/> = <see cref="LineNet"/> +
/// <see cref="LineVat"/> + <see cref="DepositAmount"/>.
/// </summary>
/// <remarks>
/// <paramref name="CounterpartyName"/> is null on team orders. <paramref name="HoldedRevenueAccountNum"/> is null
/// until Acountax issues one. <paramref name="UnitPrice"/> is the effective price excluding VAT;
/// <paramref name="DepositAmount"/> covers the whole line (qty × per-unit deposit).
/// </remarks>
public sealed record AccountingOrderLineDto(
    int Year,
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    string CounterpartyLabel,
    string? CounterpartyName,
    string? CounterpartyVatId,
    string? CounterpartyCountryCode,
    OrderState OrderState,
    string? IssuedInvoiceNumber,
    Guid LineId,
    Guid ProductId,
    string ProductName,
    int? HoldedRevenueAccountNum,
    int Qty,
    decimal UnitPrice,
    decimal VatRatePercent,
    decimal LineGross,
    decimal LineNet,
    decimal LineVat,
    decimal DepositAmount,
    Instant AddedAt);

/// <summary>
/// Only camp orders carry payments, so <paramref name="CounterpartyType"/> is always <c>Camp</c> today.
/// <paramref name="AmountEur"/> is signed: a refund is negative. A <c>DepositReturn</c> method credits a returned
/// deposit; no money moved.
/// </summary>
public sealed record AccountingPaymentDto(
    int Year,
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    string CounterpartyLabel,
    Guid PaymentId,
    decimal AmountEur,
    string Method,
    string? StripePaymentIntentId,
    string? ExternalRef,
    Instant ReceivedAt);
