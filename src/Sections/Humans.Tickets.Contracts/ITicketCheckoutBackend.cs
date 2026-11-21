using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// <para>
/// The checkout port: whatever takes the money for a Humans ticket order. Humans owns the catalogue, the price, the
/// named ticket and the resale queue; a backend only charges the lines it is given, reports the outcome and refunds an
/// exact amount. Each backend is its own adapter section registering one implementation; Tickets resolves the one an
/// event uses by <see cref="Key"/>.
/// </para>
/// <para>
/// Amounts are minor units. An adapter never changes a price: if its backend cannot charge exactly the requested
/// lines, it throws <see cref="TicketCheckoutException"/>. <see cref="CheckoutRequest.OrderId"/> and
/// <see cref="RefundRequest.RefundId"/> are idempotency keys: a repeat with the same id must not charge or refund twice.
/// The buyer pays exactly the lines: no payment surcharge, ever.
/// </para>
/// <para>
/// Late payments: Tickets owns the hold. When <see cref="CheckoutRequest.ExpiresAt"/> passes unpaid, Tickets calls
/// <see cref="CancelAsync"/> so the backend stops taking money. A payment that lands anyway is reported as
/// <see cref="CheckoutState.Paid"/>, never refunded or kept silently by the adapter; Tickets issues the tickets and flags
/// the order for the ticketing team.
/// </para>
/// </summary>
public interface ITicketCheckoutBackend : IApplicationService
{
    /// <summary>Stable, lowercase, stored on every order: <c>boxoffice</c>, <c>pretix</c>, <c>hievents</c>.</summary>
    string Key { get; }

    /// <summary>False when the adapter's credentials are unset; Tickets refuses to open checkouts on it.</summary>
    bool IsConfigured { get; }

    [ExternalWrite]
    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default);

    /// <summary>The backend's own view of the order, which is the truth; webhooks only nudge Tickets to call this.</summary>
    Task<CheckoutStatus> GetStatusAsync(string backendOrderId, CancellationToken ct = default);

    /// <summary>
    /// A repeat of a refund id answers that refund's current state without refunding again. Throws when the backend
    /// gave no answer, so Tickets keeps the refund pending and repeats it.
    /// Never throws for a refund the backend declines: that is <see cref="RefundState.Failed"/>. A backend that may not
    /// move money itself (Humans never moves money out of Stripe, <c>memory/architecture/stripe-money-out.md</c>)
    /// answers <see cref="RefundState.ManualRequired"/> with the page where a person issues it; the refund then shows up
    /// in <see cref="CheckoutStatus.RefundedMinor"/>.
    /// </summary>
    [ExternalWrite]
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default);

    /// <summary>Closes an unpaid checkout. A no-op when it is already closed; throws when it is already paid.</summary>
    [ExternalWrite]
    Task CancelAsync(string backendOrderId, CancellationToken ct = default);

    /// <summary>
    /// GDPR erasure: removes the name and email <paramref name="email"/> belongs to wherever it appears on the order
    /// (buyer or attendee), keeping amounts and dates. Idempotent. Throws when it could not, so the erasure is retried;
    /// a no-op only where the backend keeps the data under its own legal basis (Stripe as payment processor).
    /// </summary>
    [ExternalWrite]
    Task ErasePersonalDataAsync(string backendOrderId, string email, CancellationToken ct = default);

    /// <summary>What an admin needs to connect this backend, shown on Tickets' backends page. Never throws.</summary>
    CheckoutBackendSetup Setup { get; }

    /// <summary>
    /// Reads the backend and changes nothing: the credentials work and, given <paramref name="backendEventRef"/>, that
    /// event is ready to sell through Humans. Never throws; every problem is a finding that is not ok.
    /// </summary>
    Task<IReadOnlyList<CheckoutBackendFinding>> CheckAsync(string? backendEventRef, CancellationToken ct = default);
}

/// <summary>
/// <paramref name="Hosting"/> says who runs the backend and where: Humans' own instance, the vendor's hosted service,
/// or Stripe. <paramref name="WebhookPath"/> is the path and query on the Humans site to enter in the backend, secrets
/// as placeholders; null when it needs no webhook. <paramref name="Notes"/> are setup steps the settings cannot show.
/// </summary>
public sealed record CheckoutBackendSetup(
    string DisplayName,
    string Hosting,
    Uri? AdminUrl,
    IReadOnlyList<CheckoutBackendSetting> Settings,
    string? WebhookPath,
    IReadOnlyList<string> Notes);

/// <summary>
/// One server setting the backend reads. <paramref name="Name"/> is the configuration key or environment variable;
/// <paramref name="Value"/> is null when unset or secret, so a secret only ever shows as set or missing.
/// </summary>
public sealed record CheckoutBackendSetting(string Name, string Purpose, bool IsSecret, bool IsSet, string? Value);

public sealed record CheckoutBackendFinding(bool Ok, string Text);

/// <summary>
/// <paramref name="EventId"/> is the Humans (Settings) event the order is for; several events may sell at once.
/// <paramref name="BackendEventRef"/> is the event's id inside the backend (a pretix event slug, a Hi.Events event id),
/// set per event in Tickets; null for a backend that needs none. <paramref name="ReturnUrl"/> is where the buyer lands
/// after paying or abandoning; Tickets reads the outcome from <see cref="ITicketCheckoutBackend.GetStatusAsync"/>.
/// </summary>
public sealed record CheckoutRequest(
    Guid OrderId,
    Guid EventId,
    string? BackendEventRef,
    string BuyerName,
    string BuyerEmail,
    string Currency,
    string Culture,
    IReadOnlyList<CheckoutLine> Lines,
    Instant ExpiresAt,
    Uri ReturnUrl);

/// <summary>
/// <paramref name="VatRate"/> is a fraction (0.10 for 10%). Donation lines carry 0. <paramref name="AttendeeName"/> is
/// the legal name printed on a ticket line, null on donations.
/// </summary>
public sealed record CheckoutLine(
    CheckoutLineKind Kind,
    string Description,
    long AmountMinor,
    decimal VatRate,
    string? AttendeeName);

public enum CheckoutLineKind
{
    Ticket,
    Donation,
}

/// <summary><paramref name="AdminUrl"/> is the order in the backend's own admin, for the ticketing team; null when there is none.</summary>
public sealed record CheckoutSession(string BackendOrderId, Uri PaymentUrl, Uri? AdminUrl = null);

/// <summary>
/// <para>
/// <paramref name="FeeMinor"/> is what the payment provider kept from this payment (its processing cost, never charged
/// to the buyer); null when the backend cannot tell.
/// </para>
/// <para>
/// <paramref name="RefundedMinor"/> is everything refunded against the order so far, however it was issued, so a
/// refund made in the backend's own dashboard is still seen; it moves only once money has actually gone back. Tickets
/// settles its <see cref="RefundState.Pending"/> and <see cref="RefundState.ManualRequired"/> refunds from it, oldest
/// first, and repeats each pending refund <paramref name="RefundedMinor"/> does not cover, under its own id, to learn
/// whether it failed. <paramref name="RefundFailed"/> (a refund the backend accepted has since failed) is informational.
/// </para>
/// </summary>
public sealed record CheckoutStatus(
    CheckoutState State,
    long? FeeMinor,
    string? PaymentMethod,
    Instant? PaidAt,
    long RefundedMinor = 0,
    bool RefundFailed = false);

public enum CheckoutState
{
    Pending,
    Paid,
    Failed,
    Expired,
    Cancelled,
}

public sealed record RefundRequest(Guid RefundId, string BackendOrderId, long AmountMinor, string Reason);

/// <summary>
/// <paramref name="BackendRefundId"/> is required whenever the backend accepted the refund
/// (<see cref="RefundState.Pending"/> or <see cref="RefundState.Succeeded"/>). Tickets reads a pending refund without
/// one as "the call never got an answer" and resends it under the same id on every reconcile run, so an adapter that
/// cannot name the refund must throw instead. <paramref name="ManualRefundUrl"/> is set only for
/// <see cref="RefundState.ManualRequired"/>.
/// </summary>
public sealed record RefundResult(
    RefundState State,
    string? BackendRefundId,
    string? FailureReason,
    Uri? ManualRefundUrl = null);

public enum RefundState
{
    Succeeded,
    /// <summary>Accepted, still being sent (a bank transfer, a SEPA refund).</summary>
    Pending,
    Failed,
    /// <summary>A person issues it in the backend's dashboard; Tickets reads it back through the order status.</summary>
    ManualRequired,
}

/// <summary>The backend cannot do what was asked as asked: an exact price, a currency, a method.</summary>
public sealed class TicketCheckoutException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Adapters call this when their webhook fires. Tickets then calls
/// <see cref="ITicketCheckoutBackend.GetStatusAsync"/>, so the webhook is only a nudge, an unverified or replayed one
/// does no harm, and a missed one is caught by Tickets' own poll.
/// </summary>
public interface ITicketCheckoutEvents : IApplicationService
{
    Task CheckoutChangedAsync(string backendKey, string backendOrderId, CancellationToken ct = default);
}
