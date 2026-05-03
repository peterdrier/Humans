namespace Humans.Application.Interfaces;

/// <summary>
/// Stripe connector. Fee/PI reads (Tickets account) and Checkout Session creation (Store account).
/// SDK types do not cross this seam — see <c>StripeConnectorArchitectureTests</c>.
/// </summary>
public interface IStripeService
{
    /// <summary>True when the Tickets-account key is set (fee enrichment available).</summary>
    bool IsConfigured { get; }

    /// <summary>True when the Store-account key is set (Checkout Session creation available).</summary>
    bool IsStoreCheckoutConfigured { get; }

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
}

/// <summary>Fee and payment method data extracted from a Stripe PaymentIntent's charge.</summary>
public record StripePaymentDetails(
    string PaymentMethod,
    string? PaymentMethodDetail,
    decimal StripeFee,
    decimal ApplicationFee);
