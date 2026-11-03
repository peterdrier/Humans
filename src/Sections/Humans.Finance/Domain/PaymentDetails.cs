using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>The member's payment details, owned by Finance.</summary>
internal sealed class PaymentDetails
{
    public Guid UserId { get; init; }

    /// <summary>Normalized. Null records a removal.</summary>
    public string? Iban { get; set; }

    public Instant UpdatedAt { get; set; }
}
