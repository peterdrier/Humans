using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Stripe.Contracts;

public interface IStripeService : IApplicationService
{
    /// <summary>True when the Tickets-account key is set (fee enrichment available).</summary>
    bool IsConfigured { get; }

    /// <summary>True when the Store-account key is set (Checkout Session creation available).</summary>
    bool IsStoreCheckoutConfigured { get; }

    /// <summary>True when the Store webhook signing secret is set (webhook signature verification available).</summary>
    bool IsStoreWebhookConfigured { get; }

    /// <summary>
    /// EUR fees and payment method for a PaymentIntent; missing or non-EUR currency throws. Null only when the
    /// Tickets key is unset or lacks the scope, or the intent has no charge or balance transaction yet. Every
    /// other Stripe failure propagates.
    /// </summary>
    Task<StripePaymentDetails?> GetPaymentDetailsAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// Creates a hosted-checkout session; <paramref name="storeOrderId"/> round-trips in the
    /// <c>humans_store_order_id</c> metadata. A non-null <paramref name="checkoutAttemptId"/> is the Stripe idempotency
    /// key, so repeating an attempt with identical arguments replays its original session, returned in its live state. A
    /// non-null
    /// <paramref name="supersededSessionId"/> is expired first; when that session has already completed, no new session
    /// is created and the completed one is returned (Status <c>complete</c>, no Url). Throws on Stripe failure; a null
    /// <paramref name="customerEmail"/> lets Stripe collect it.
    /// </summary>
    [ExternalWrite]
    Task<StoreCheckoutSessionData> CreateCheckoutSessionAsync(
        Guid storeOrderId,
        decimal amountEur,
        string successUrl,
        string cancelUrl,
        string? customerEmail,
        string lineItemDescription,
        CancellationToken ct = default,
        Guid? checkoutAttemptId = null,
        string? supersededSessionId = null);

    /// <summary>
    /// <paramref name="body"/> must be the raw request body. Null when the signing secret is unset, the signature
    /// is invalid, the payload is malformed or incomplete, or a session lacks EUR currency; never throws.
    /// </summary>
    StoreCheckoutWebhookEvent? ParseStoreCheckoutEvent(string body, string signature);

    /// <summary>
    /// Every Store-account session, newest first. Null means unknown, not "none": the Store key is unset or
    /// lacks the read scope. A session without EUR currency rejects the whole list; other Stripe failures propagate.
    /// </summary>
    Task<IReadOnlyList<StoreCheckoutSessionData>?> ListStoreCheckoutSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every refund Stripe holds against a Store-account PaymentIntent, read with the org read key. Null means
    /// unknown, not "none": the key or the Store account is not configured, or the key lacks the refund read scope.
    /// A refund without EUR currency rejects the whole list; other Stripe failures propagate. Read only: Humans never
    /// issues a refund (<c>memory/architecture/stripe-money-out.md</c>).
    /// </summary>
    Task<IReadOnlyList<StoreRefundData>?> ListStoreRefundsAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// The settled charge behind a Store-account PaymentIntent, read with the org read key: its gross amount and the
    /// second it was created, which is how the Holded gateway feed line for it is found. Null means unknown: the key or
    /// the Store account is not configured, the key lacks the read scope, or the intent has no charge yet.
    /// </summary>
    Task<StoreChargeData?> GetStoreChargeAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>The Stripe dashboard page for a Store-account payment, where a person issues its refund.</summary>
    string StorePaymentDashboardUrl(string paymentIntentId);

    /// <summary>
    /// Every balance movement and payout created in [<paramref name="from"/>, <paramref name="to"/>) on the
    /// <c>Stripe:MirrorAccounts</c> entry named <paramref name="accountName"/>, read live, each movement with the
    /// PaymentIntent behind it. Null when that account or its key is not configured. A Stripe refusal throws
    /// <see cref="InvalidOperationException"/>; an unreachable Stripe throws <see cref="HttpRequestException"/>.
    /// </summary>
    Task<StripeAccountLedger?> GetAccountLedgerAsync(string accountName, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>
    /// What Stripe kept, in minor units, on a PaymentIntent of the named account. Null when unknown: the account is
    /// not configured, the charge has no balance transaction yet, or Stripe could not be read (logged). Never throws
    /// but on cancellation, so a fee lookup never holds up a paid order.
    /// </summary>
    Task<long?> GetAccountPaymentFeeAsync(string accountName, string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// The fee, as <see cref="GetAccountPaymentFeeAsync"/>, of the succeeded PaymentIntent of the named account whose
    /// metadata holds every pair in <paramref name="metadata"/>, for a vendor that does not expose the intent's id.
    /// Stripe's search trails a new payment by up to a minute, so for a payment created since
    /// <paramref name="createdSince"/> within the last two hours, the intents created since then are listed too.
    /// </summary>
    Task<long?> FindAccountPaymentFeeAsync(
        string accountName, IReadOnlyDictionary<string, string> metadata, Instant createdSince, CancellationToken ct = default);

    /// <summary>One finding per read scope <see cref="GetAccountLedgerAsync"/> needs on the named account. Never throws.</summary>
    Task<IReadOnlyList<StripeAccountCheck>> CheckAccountAsync(string accountName, CancellationToken ct = default);
}

/// <summary>A Stripe account's movements and payouts for a period, all amounts in minor units.</summary>
public sealed record StripeAccountLedger(IReadOnlyList<StripeLedgerLine> Lines, IReadOnlyList<StripeLedgerPayout> Payouts);

/// <summary>
/// One balance transaction. <paramref name="ReportingCategory"/> is Stripe's (<c>charge</c>, <c>refund</c>,
/// <c>dispute</c>, <c>payout</c>, <c>fee</c>, …). <paramref name="AmountMinor"/> is signed; the balance moves by amount
/// minus <paramref name="FeeMinor"/>. <paramref name="Payment"/> is the PaymentIntent behind a charge, refund or dispute.
/// </summary>
public sealed record StripeLedgerLine(
    string Id, Instant CreatedAt, string ReportingCategory, long AmountMinor, long FeeMinor, string? Description,
    StripeLedgerPayment? Payment);

/// <summary>A PaymentIntent's id, description and metadata: how a vendor names its own order on it.</summary>
public sealed record StripeLedgerPayment(string PaymentIntentId, string? Description, IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// One payout to the bank. <paramref name="ContentsNetMinor"/> and <paramref name="ContentsCount"/> sum the movements
/// it pays out; null and zero unless Stripe has finished reconciling an automatic payout.
/// </summary>
public sealed record StripeLedgerPayout(
    string Id, Instant CreatedAt, Instant ArrivalAt, long AmountMinor, string Status, long? ContentsNetMinor, int ContentsCount);

/// <summary>One line of a key check, for a backends page.</summary>
public sealed record StripeAccountCheck(bool Ok, string Text);

/// <summary>
/// <paramref name="Status"/> is Stripe's refund status (<c>pending</c>, <c>requires_action</c>, <c>succeeded</c>,
/// <c>failed</c>, <c>canceled</c>). <paramref name="AmountEur"/> is positive, already converted from minor units.
/// </summary>
public sealed record StoreRefundData(string RefundId, string PaymentIntentId, decimal AmountEur, string Status, Instant CreatedAt);

/// <summary><paramref name="AmountEur"/> is the gross charge, positive, converted from minor units.</summary>
public sealed record StoreChargeData(decimal AmountEur, Instant CreatedAt);

/// <summary>One amount per Stripe balance-transaction fee bucket, in EUR.</summary>
/// <remarks>
/// <paramref name="PaymentMethodDetail"/> is the card brand for cards, else null. <paramref name="StripeFee"/> is
/// <c>stripe_fee</c> plus <c>payment_method_passthrough_fee</c>. <paramref name="ApplicationFee"/> is Ticket Tailor's
/// <c>application_fee</c>. <paramref name="FeeTax"/> is VAT Stripe charged on its fees; <paramref name="WithheldTax"/>
/// is tax withheld from the payout.
/// </remarks>
public record StripePaymentDetails(
    string PaymentMethod,
    string? PaymentMethodDetail,
    decimal StripeFee,
    decimal ApplicationFee,
    decimal FeeTax,
    decimal WithheldTax);

public enum StoreCheckoutEventKind
{
    /// <summary>Subscribed but not categorized.</summary>
    Other,
    /// <summary>Card payments settle here; async methods only capture the mandate.</summary>
    CheckoutSessionCompleted,
    CheckoutSessionAsyncPaymentSucceeded,
    CheckoutSessionAsyncPaymentFailed,
    CheckoutSessionExpired,
}

/// <summary><paramref name="Session"/> is null for events that carry no session.</summary>
public sealed record StoreCheckoutWebhookEvent(
    string EventId,
    StoreCheckoutEventKind Kind,
    StoreCheckoutSessionData? Session);

/// <summary>
/// Optional fields are null when Stripe omitted them or they failed to parse; the caller decides which it needs.
/// <paramref name="OrderId"/> comes from the <c>humans_store_order_id</c> metadata. <paramref name="AmountEur"/> is
/// already converted from minor units. <paramref name="Status"/> is Stripe's session status (<c>open</c>, <c>complete</c>,
/// <c>expired</c>); <paramref name="Url"/> is the hosted-checkout URL, set only while the session is open.
/// </summary>
public sealed record StoreCheckoutSessionData(
    string SessionId,
    Guid? OrderId,
    string? PaymentIntentId,
    decimal? AmountEur,
    string? PaymentStatus = null,
    Instant? CreatedAt = null,
    string? Status = null,
    string? Url = null);
