namespace Humans.Application.Interfaces;

/// <summary>
/// Stripe connector. Fee/PI reads (Tickets account) and Checkout Session creation (Store account).
/// SDK types do not cross this seam — see <c>StripeConnectorArchitectureTests</c>.
/// </summary>
public interface IStripeService : IApplicationService
{
    /// <summary>True when the Tickets-account key is set (fee enrichment available).</summary>
    bool IsConfigured { get; }

    /// <summary>True when the Store-account key is set (Checkout Session creation available).</summary>
    bool IsStoreCheckoutConfigured { get; }

    /// <summary>True when the Store webhook signing secret is set (webhook signature verification available).</summary>
    bool IsStoreWebhookConfigured { get; }

    /// <summary>
    /// Look up a PaymentIntent and return fee breakdown and payment method details.
    /// Returns null if the PaymentIntent has no successful charge or the key lacks the required scope.
    /// </summary>
    Task<StripePaymentDetails?> GetPaymentDetailsAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// Create a Stripe Checkout Session for a Store order payment and return the hosted-checkout URL.
    /// Sets metadata <c>humans_store_order_id</c> so the webhook can resolve back to the order.
    /// Throws on Stripe API failure (caller surfaces a friendly error to the user).
    /// </summary>
    /// <param name="storeOrderId">Identifies the StoreOrder; round-trips via session metadata.</param>
    /// <param name="amountEur">Amount to charge in EUR. Must be > 0.</param>
    /// <param name="successUrl">Absolute URL Stripe redirects to on payment success.</param>
    /// <param name="cancelUrl">Absolute URL Stripe redirects to if the user cancels checkout.</param>
    /// <param name="customerEmail">Optional pre-fill for the Stripe-collected email; pass null to let Stripe collect it.</param>
    /// <param name="lineItemDescription">Human-readable description shown on the Stripe-hosted page and receipt.</param>
    Task<string> CreateCheckoutSessionAsync(
        Guid storeOrderId,
        decimal amountEur,
        string successUrl,
        string cancelUrl,
        string? customerEmail,
        string lineItemDescription,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies a Stripe webhook signature against <c>STRIPE_STORE_WEBHOOK_SECRET</c> and parses
    /// the payload into an Application-layer DTO so the Web layer never imports Stripe SDK types
    /// (design-rules §15i — connector pattern). Returns <c>null</c> when the signature is invalid;
    /// the implementation logs the failure reason internally.
    /// </summary>
    /// <param name="body">Raw request body as received from Stripe.</param>
    /// <param name="signature">Value of the <c>Stripe-Signature</c> header.</param>
    StoreCheckoutWebhookEvent? ParseStoreCheckoutEvent(string body, string signature);
}

/// <summary>Fee and payment method data extracted from a Stripe PaymentIntent's charge.</summary>
public record StripePaymentDetails(
    string PaymentMethod,
    string? PaymentMethodDetail,
    decimal StripeFee,
    decimal ApplicationFee);

/// <summary>Categorized Stripe Checkout webhook event the Store cares about.</summary>
public enum StoreCheckoutEventKind
{
    /// <summary>An event we are subscribed to but do not categorize (forward-compatibility).</summary>
    Other,
    /// <summary>checkout.session.completed — sync settlement (card) or mandate captured (async).</summary>
    CheckoutSessionCompleted,
    /// <summary>checkout.session.async_payment_succeeded — async settlement cleared.</summary>
    CheckoutSessionAsyncPaymentSucceeded,
    /// <summary>checkout.session.async_payment_failed — async settlement bounced.</summary>
    CheckoutSessionAsyncPaymentFailed,
    /// <summary>checkout.session.expired — session timed out without payment.</summary>
    CheckoutSessionExpired,
}

/// <summary>
/// Application-layer projection of a Stripe Checkout webhook event. Carries only the fields
/// the Store webhook needs; SDK types are extracted on the Infrastructure side of the seam.
/// </summary>
/// <param name="EventId">Stripe event id (<c>evt_*</c>) for log correlation.</param>
/// <param name="Kind">Categorized event type — controller routes on this.</param>
/// <param name="Session">Session payload, when the event carries one. Null for events without a session.</param>
public sealed record StoreCheckoutWebhookEvent(
    string EventId,
    StoreCheckoutEventKind Kind,
    StoreCheckoutSessionData? Session);

/// <summary>
/// Per-session fields from a <c>checkout.session.*</c> event. Optional fields are null when
/// Stripe didn't send them or they couldn't be parsed; the caller decides which are required.
/// </summary>
/// <param name="SessionId">Stripe session id (<c>cs_*</c>) for log correlation.</param>
/// <param name="OrderId">Round-tripped via <c>session.metadata['humans_store_order_id']</c>; null if missing or malformed.</param>
/// <param name="PaymentIntentId">Stripe PaymentIntent id; null if Stripe didn't include one.</param>
/// <param name="AmountEur">Total amount in EUR (already converted from minor units); null if Stripe didn't send a total.</param>
public sealed record StoreCheckoutSessionData(
    string SessionId,
    Guid? OrderId,
    string? PaymentIntentId,
    decimal? AmountEur);
