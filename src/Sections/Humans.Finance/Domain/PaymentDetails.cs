using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// Replaces <c>profiles.Iban</c>, which Users keeps unwritten until <c>finance.iban-to-payment-details</c> is done in
/// production.
/// </summary>
internal sealed class PaymentDetails
{
    public Guid UserId { get; init; }

    /// <summary>Normalized. Null records a removal, so the read never falls back to the legacy profile value.</summary>
    public string? Iban { get; set; }

    public Instant UpdatedAt { get; set; }
}
