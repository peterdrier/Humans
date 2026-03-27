namespace Humans.Application.Interfaces;

/// <summary>
/// Reads payment details from Stripe for fee tracking and payment method attribution.
/// </summary>
public interface IStripeService
{
    bool IsConfigured { get; }

    /// <summary>
    /// Look up a PaymentIntent and return fee breakdown and payment method details.
    /// Returns null if the PaymentIntent has no successful charge.
    /// </summary>
    Task<StripePaymentDetails?> GetPaymentDetailsAsync(string paymentIntentId, CancellationToken ct = default);
}

/// <summary>Fee and payment method data extracted from a Stripe PaymentIntent's charge.</summary>
public record StripePaymentDetails(
    string PaymentMethod,
    string? PaymentMethodDetail,
    decimal StripeFee,
    decimal ApplicationFee);
