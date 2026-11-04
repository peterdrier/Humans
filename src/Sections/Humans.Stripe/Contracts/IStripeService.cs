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
    /// key, so repeating an attempt with identical arguments replays its original session. A non-null
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
    /// Expires an open Store-account session and returns it. A session that is no longer open is returned as Stripe holds
    /// it (Status <c>complete</c> or <c>expired</c>). Throws on Stripe failure.
    /// </summary>
    [ExternalWrite]
    Task<StoreCheckoutSessionData> ExpireCheckoutSessionAsync(string sessionId, CancellationToken ct = default);

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
}

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
