using NodaTime;

namespace Humans.Store.Contracts;

/// <summary>
/// Priced the way the admin summary prices it. <see cref="LineGross"/> = <see cref="LineNet"/> +
/// <see cref="LineVat"/> + <see cref="DepositAmount"/>.
/// </summary>
/// <remarks>
/// <paramref name="CounterpartyName"/> is null on team orders; <paramref name="TeamId"/> is set on team orders only.
/// The three Holded account numbers are the product's today, each null until set in the catalog. <paramref name="UnitPrice"/> is the effective price excluding VAT;
/// <paramref name="SnapshotUnitPrice"/> is the stored line price excluding VAT: the catalog price when the line was added,
/// or the frozen price once the invoice is issued.
/// <paramref name="DepositAmount"/> covers the whole line (qty × per-unit deposit).
/// </remarks>
public sealed record AccountingOrderLineDto(
    int Year,
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    Guid? TeamId,
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
    int? HoldedExpenseAccountNum,
    int? HoldedTransferAccountNum,
    int Qty,
    decimal UnitPrice,
    decimal SnapshotUnitPrice,
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

/// <summary>
/// One audit entry in an order's history: the same entries the order page shows its admins, plus price changes to the
/// order's products since it was created. <paramref name="ActorUserId"/> is null for automation (the Stripe webhook);
/// <paramref name="Description"/> is the stored English audit text.
/// </summary>
public sealed record AccountingOrderHistoryDto(
    int Year,
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    string CounterpartyLabel,
    Instant OccurredAt,
    string Action,
    Guid? ActorUserId,
    string? ActorDisplayName,
    string Description);
