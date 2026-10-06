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
/// <param name="Year">The event year for the order.</param>
/// <param name="OrderId">The order identifier.</param>
/// <param name="CounterpartyType">Whether the order's counterparty is a camp or team.</param>
/// <param name="CounterpartyVatId">The counterparty's VAT identifier, if recorded.</param>
/// <param name="CounterpartyCountryCode">The counterparty's country code, if recorded.</param>
/// <param name="OrderState">The order's current state.</param>
/// <param name="LineId">The order line identifier.</param>
/// <param name="ProductId">The ordered product identifier.</param>
/// <param name="ProductName">The product name recorded for the order line.</param>
/// <param name="Qty">The quantity on the order line.</param>
/// <param name="VatRatePercent">The VAT rate applied to the line.</param>
/// <param name="LineGross">The line total including VAT and deposit.</param>
/// <param name="LineNet">The line total excluding VAT.</param>
/// <param name="LineVat">The VAT amount for the line.</param>
/// <param name="AddedAt">When the order line was added.</param>
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
/// <param name="Method">The <c>PaymentMethod</c> name: <c>Stripe</c>, <c>BankTransfer</c>, <c>Manual</c>,
/// <c>DepositReturn</c> (a returned deposit credited to the order — no money moved) or <c>Refund</c>.</param>
/// <param name="Year">The event year for the order.</param>
/// <param name="OrderId">The order identifier this payment belongs to.</param>
/// <param name="CounterpartyType">The type of counterparty for the order.</param>
/// <param name="CounterpartyLabel">The camp or team name shown for the counterparty.</param>
/// <param name="PaymentId">The payment identifier.</param>
/// <param name="StripePaymentIntentId">The Stripe PaymentIntent identifier, if present.</param>
/// <param name="ExternalRef">An external payment reference, if present.</param>
/// <param name="ReceivedAt">When the payment was received.</param>
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
