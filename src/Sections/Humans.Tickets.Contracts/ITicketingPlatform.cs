using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// <para>
/// The ticketing platform port: whatever takes the money for a Humans order. Humans owns the catalogue, the price, the
/// named ticket and the resale queue; a ticketing platform only charges the lines it is given, reports the outcome and refunds an
/// exact amount. Each platform is its own adapter section registering one implementation; Tickets resolves the one an
/// event uses by <see cref="Key"/>.
/// </para>
/// <para>
/// Amounts are minor units. An adapter never changes a price: if its platform cannot charge exactly the requested
/// lines, it throws <see cref="TicketingPlatformException"/>. <see cref="PlatformOrderRequest.OrderId"/> and
/// <see cref="RefundRequest.RefundId"/> are idempotency keys: a repeat with the same id must not charge or refund twice.
/// The buyer pays exactly the lines: never a fee for the payment method.
/// </para>
/// <para>
/// Late payments: Tickets owns the hold. When <see cref="PlatformOrderRequest.ExpiresAt"/> passes unpaid, Tickets calls
/// <see cref="CancelAsync"/> so the platform stops taking money. A payment that lands anyway is reported as
/// <see cref="PlatformOrderState.Paid"/>, never refunded or kept silently by the adapter; Tickets issues the tickets and flags
/// the order for the ticketing team.
/// </para>
/// </summary>
public interface ITicketingPlatform : IApplicationService
{
    /// <summary>Stable, lowercase, stored on every order: <c>boxoffice</c>, <c>pretix</c>, <c>hievents</c>.</summary>
    string Key { get; }

    /// <summary>False when the adapter's credentials are unset; Tickets refuses to open orders on it.</summary>
    bool IsConfigured { get; }

    [ExternalWrite]
    Task<PlatformOrderSession> CreateOrderAsync(PlatformOrderRequest request, CancellationToken ct = default);

    /// <summary>The platform's own view of the order, which is the truth; webhooks only nudge Tickets to call this.</summary>
    Task<PlatformOrderStatus> GetStatusAsync(string platformOrderId, CancellationToken ct = default);

    /// <summary>
    /// A repeat of a refund id answers that refund's current state without refunding again. Throws when the platform
    /// gave no answer, so Tickets keeps the refund pending and repeats it.
    /// Never throws for a refund the platform declines: that is <see cref="RefundState.Failed"/>. A platform that may not
    /// move money itself (Humans never moves money out of Stripe, <c>memory/architecture/stripe-money-out.md</c>)
    /// answers <see cref="RefundState.ManualRequired"/> with the page where a person issues it; the refund then shows up
    /// in <see cref="PlatformOrderStatus.RefundedMinor"/>.
    /// </summary>
    [ExternalWrite]
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default);

    /// <summary>Closes an unpaid platform order. A no-op when it is already closed; throws when it is already paid.</summary>
    [ExternalWrite]
    Task CancelAsync(string platformOrderId, CancellationToken ct = default);

    /// <summary>
    /// GDPR erasure: removes the name and email <paramref name="email"/> belongs to wherever it appears on the order
    /// (buyer or holder), keeping amounts and dates. Idempotent. Throws when it could not, so the erasure is retried;
    /// a no-op only where the platform keeps the data under its own legal basis (Stripe as payment processor).
    /// </summary>
    [ExternalWrite]
    Task ErasePersonalDataAsync(string platformOrderId, string email, CancellationToken ct = default);

    /// <summary>What an admin needs to connect this platform, shown on Tickets' platforms page. Never throws.</summary>
    PlatformSetup Setup { get; }

    /// <summary>
    /// Reads the platform and changes nothing: the credentials work and, given <paramref name="platformEventRef"/>, that
    /// event is ready to sell through Humans. Never throws; every problem is a finding that is not ok.
    /// </summary>
    Task<IReadOnlyList<PlatformFinding>> CheckAsync(string? platformEventRef, CancellationToken ct = default);

    /// <summary>
    /// The platform's side of Tickets' financial reconciliation: every order it holds that was created in
    /// [<paramref name="from"/>, <paramref name="to"/>), for <paramref name="platformEventRef"/> where the platform keeps
    /// orders per event, read from the platform itself. Includes orders Humans did not open (one made in the platform's
    /// own dashboard), which reconcile as extra. Changes nothing; throws when any page cannot be read, so a partial
    /// list is never taken for the whole.
    /// </summary>
    Task<IReadOnlyList<PlatformOrderSummary>> ListOrdersAsync(
        string? platformEventRef, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>
    /// The platform event ref <paramref name="platformOrderId"/> was sold under, so an order is still reconciled after its
    /// event's ticketing settings moved to another platform event or platform; null for a platform that keeps no events.
    /// </summary>
    string? EventRefOf(string platformOrderId);

    /// <summary>
    /// The payment account's own ledger for Tickets' fee and payout reconciliation: every balance movement and every
    /// payout created in [<paramref name="from"/>, <paramref name="to"/>), read from the payment provider itself.
    /// Null when the platform keeps its payment account to itself, so Humans cannot read it. Changes nothing; throws
    /// when any page cannot be read, so a partial ledger is never taken for the whole.
    /// </summary>
    Task<PaymentLedger?> GetPaymentLedgerAsync(Instant from, Instant to, CancellationToken ct = default);
}

/// <summary>
/// The payment account for a period: <paramref name="Entries"/> is every balance movement in it, payouts included;
/// <paramref name="Payouts"/> every payout to the bank created in it.
/// </summary>
public sealed record PaymentLedger(IReadOnlyList<PaymentLedgerEntry> Entries, IReadOnlyList<PaymentPayout> Payouts);

/// <summary>
/// One balance movement. <paramref name="AmountMinor"/> is signed, money into the account positive (a charge, a
/// chargeback won back) and out negative (a refund, a chargeback, a payout). <paramref name="FeeMinor"/> is what the provider kept
/// on it; the balance moves by amount minus fee. <paramref name="HumansOrderId"/> is the order the payment carries in
/// its metadata, null when it carries none.
/// </summary>
public sealed record PaymentLedgerEntry(
    string Id,
    Instant CreatedAt,
    PaymentLedgerEntryKind Kind,
    long AmountMinor,
    long FeeMinor,
    Guid? HumansOrderId,
    string? Description)
{
    /// <summary>Maps Stripe reporting categories consistently across ticketing adapters.</summary>
    public static PaymentLedgerEntryKind KindOf(string reportingCategory) => reportingCategory switch
    {
        "charge" or "charge_failure" or "partial_capture_reversal" => PaymentLedgerEntryKind.Charge,
        "refund" or "refund_failure" => PaymentLedgerEntryKind.Refund,
        "dispute" or "dispute_reversal" => PaymentLedgerEntryKind.Chargeback,
        "fee" => PaymentLedgerEntryKind.Fee,
        "payout" or "payout_reversal" => PaymentLedgerEntryKind.Payout,
        _ => PaymentLedgerEntryKind.Other,
    };
}

public enum PaymentLedgerEntryKind
{
    /// <summary>A buyer's payment.</summary>
    Charge,
    /// <summary>A refund, or a refund that failed and came back.</summary>
    Refund,
    /// <summary>A chargeback taken, or given back when won.</summary>
    Chargeback,
    /// <summary>A fee charged on its own, not on a payment.</summary>
    Fee,
    /// <summary>Money paid out to the bank, or a failed payout coming back.</summary>
    Payout,
    /// <summary>Anything else the provider moved (an adjustment, a top-up).</summary>
    Other,
}

/// <summary>
/// One payout to the bank. <paramref name="AmountMinor"/> is what it sends, positive. <paramref name="ContentsNetMinor"/>
/// is the net of the balance movements the provider says it pays out, null when the provider cannot list them (a
/// payout made by hand, or one the provider has not finished reconciling); <paramref name="ContentsCount"/> counts them.
/// </summary>
public sealed record PaymentPayout(
    string Id,
    Instant CreatedAt,
    Instant? ArrivalAt,
    long AmountMinor,
    string Status,
    long? ContentsNetMinor,
    int ContentsCount);

/// <summary>
/// One order as the platform holds it. <paramref name="HumansOrderId"/> is the Humans order id the platform stores with
/// it, null when it has none. <paramref name="TotalMinor"/> is what the order charges (its lines, before refunds);
/// <paramref name="RefundedMinor"/> and <paramref name="ChargedBackMinor"/> mean what they do on <see cref="PlatformOrderStatus"/>.
/// <paramref name="TicketCount"/> counts ticket lines, free ones included; null when the platform cannot tell.
/// </summary>
public sealed record PlatformOrderSummary(
    string PlatformOrderId,
    Guid? HumansOrderId,
    Instant CreatedAt,
    PlatformOrderState State,
    long TotalMinor,
    int? TicketCount,
    long RefundedMinor,
    long ChargedBackMinor);

/// <summary>
/// <paramref name="Hosting"/> says who runs the platform and where: Humans' own instance, the platform maker's hosted service,
/// or Stripe. <paramref name="WebhookPath"/> is the path and query on the Humans site to enter in the platform, secrets
/// as placeholders; null when it needs no webhook. <paramref name="Notes"/> are setup steps the settings cannot show.
/// </summary>
public sealed record PlatformSetup(
    string DisplayName,
    string Hosting,
    Uri? AdminUrl,
    IReadOnlyList<PlatformSetting> Settings,
    string? WebhookPath,
    IReadOnlyList<string> Notes);

/// <summary>
/// One server setting the platform reads. <paramref name="Name"/> is the configuration key or environment variable;
/// <paramref name="Value"/> is null when unset or secret, so a secret only ever shows as set or missing.
/// </summary>
public sealed record PlatformSetting(string Name, string Purpose, bool IsSecret, bool IsSet, string? Value);

public sealed record PlatformFinding(bool Ok, string Text);

/// <summary>
/// <paramref name="EventId"/> is the Humans (Settings) event the order is for; several events may sell at once.
/// <paramref name="PlatformEventRef"/> is the event's id inside the platform (a pretix event slug, a Hi.Events event id),
/// set per event in Tickets; null for a platform that needs none. <paramref name="ReturnUrl"/> is where the buyer lands
/// after paying or abandoning; Tickets reads the outcome from <see cref="ITicketingPlatform.GetStatusAsync"/>.
/// </summary>
public sealed record PlatformOrderRequest(
    Guid OrderId,
    Guid EventId,
    string? PlatformEventRef,
    string BuyerName,
    string BuyerEmail,
    string Currency,
    string Culture,
    IReadOnlyList<PlatformOrderLine> Lines,
    Instant ExpiresAt,
    Uri ReturnUrl);

/// <summary>
/// <paramref name="VatRate"/> is a fraction (0.10 for 10%). Donation lines carry 0. <paramref name="HolderName"/> is
/// the legal name printed on a ticket line, null on donations.
/// </summary>
public sealed record PlatformOrderLine(
    OrderLineKind Kind,
    string Description,
    long AmountMinor,
    decimal VatRate,
    string? HolderName);

public enum OrderLineKind
{
    Ticket,
    Donation,
}

/// <summary><paramref name="AdminUrl"/> is the order in the platform's own admin, for the ticketing team; null when there is none.</summary>
public sealed record PlatformOrderSession(string PlatformOrderId, Uri PaymentUrl, Uri? AdminUrl = null);

/// <summary>
/// <para>
/// <paramref name="FeeMinor"/> is what the payment provider kept from this payment (its processing cost, never charged
/// to the buyer); null when the platform cannot tell.
/// </para>
/// <para>
/// <paramref name="RefundedMinor"/> is everything refunded against the order so far, however it was issued, so a
/// refund made in the platform's own dashboard is still seen; it moves only once money has actually gone back. Tickets
/// settles its <see cref="RefundState.Pending"/> and <see cref="RefundState.ManualRequired"/> refunds from it, oldest
/// first, and repeats each pending refund <paramref name="RefundedMinor"/> does not cover, under its own id, to learn
/// whether it failed. <paramref name="RefundFailed"/> (a refund the platform accepted has since failed) is informational.
/// </para>
/// <para>
/// <paramref name="ChargedBackMinor"/> is what the buyer's bank has taken back in a chargeback that the event has not won;
/// it is not a refund, and Tickets hands it to the ticketing team.
/// </para>
/// </summary>
public sealed record PlatformOrderStatus(
    PlatformOrderState State,
    long? FeeMinor,
    string? PaymentMethod,
    Instant? PaidAt,
    long RefundedMinor = 0,
    bool RefundFailed = false,
    long ChargedBackMinor = 0);

public enum PlatformOrderState
{
    Pending,
    Paid,
    Failed,
    Expired,
    Cancelled,

    /// <summary>
    /// The buyer finished the payment page with a method that settles later (a bank debit or transfer, sometimes the
    /// next day): it can no longer be cancelled and ends Paid or Failed.
    /// </summary>
    Processing,
}

public sealed record RefundRequest(Guid RefundId, string PlatformOrderId, long AmountMinor, string Reason);

/// <summary>
/// <paramref name="PlatformRefundId"/> is required whenever the platform accepted the refund
/// (<see cref="RefundState.Pending"/> or <see cref="RefundState.Succeeded"/>). Tickets reads a pending refund without
/// one as "the call never got an answer" and resends it under the same id on every reconcile run, so an adapter that
/// cannot name the refund must throw instead. <paramref name="ManualRefundUrl"/> is set only for
/// <see cref="RefundState.ManualRequired"/>.
/// </summary>
public sealed record RefundResult(
    RefundState State,
    string? PlatformRefundId,
    string? FailureReason,
    Uri? ManualRefundUrl = null);

public enum RefundState
{
    Succeeded,
    /// <summary>Accepted, still being sent (a bank transfer, a SEPA refund).</summary>
    Pending,
    Failed,
    /// <summary>A person issues it in the platform's dashboard; Tickets reads it back through the order status.</summary>
    ManualRequired,
}

/// <summary>The platform cannot do what was asked as asked: an exact price, a currency, a method.</summary>
public sealed class TicketingPlatformException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Adapters call this when their webhook fires. Tickets then calls
/// <see cref="ITicketingPlatform.GetStatusAsync"/>, so the webhook is only a nudge, an unverified or replayed one
/// does no harm, and a missed one is caught by Tickets' own poll.
/// </summary>
public interface ITicketingPlatformEvents : IApplicationService
{
    [ExternalWrite]
    Task PlatformOrderChangedAsync(string platformKey, string platformOrderId, CancellationToken ct = default);
}
