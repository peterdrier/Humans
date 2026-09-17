using NodaTime;

namespace Humans.Store.Contracts;

/// <summary>
/// One <c>store_order_lines</c> row for the accounting export, priced the way the admin
/// summary prices it. <see cref="LineGross"/> = <see cref="LineNet"/> + <see cref="LineVat"/>
/// + <see cref="DepositAmount"/>.
/// </summary>
/// <param name="CounterpartyLabel">Camp name or team name, resolved from Camps / Teams.</param>
/// <param name="CounterpartyName">The invoice counterparty typed on the order; null on team orders.</param>
/// <param name="IssuedInvoiceNumber">Holded document number once the order is invoiced, else null.</param>
/// <param name="HoldedRevenueAccountNum">The product's Holded revenue account; null until Acountax issues one.</param>
/// <param name="UnitPrice">Effective per-unit price excluding VAT.</param>
/// <param name="DepositAmount">Effective deposit for the whole line (qty × per-unit deposit), 0 when none.</param>
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
/// One settled <c>store_payments</c> row for the accounting export. Only camp orders carry
/// payments, so <paramref name="CounterpartyType"/> is always <c>Camp</c> today.
/// </summary>
/// <param name="AmountEur">Signed — a refund is negative.</param>
/// <param name="Method">The <c>PaymentMethod</c> name: <c>Stripe</c>, <c>BankTransfer</c> or <c>Manual</c>.</param>
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
