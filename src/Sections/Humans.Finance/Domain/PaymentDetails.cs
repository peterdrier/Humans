using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// The bank account a member is reimbursed to, one row per member (<c>finance_payment_details</c>).
/// Replaces <c>profiles.Iban</c>, which Users keeps unwritten and unread until its drop.
/// </summary>
internal sealed class PaymentDetails
{
    /// <summary>The member. Bare id, no FK to <c>users</c>
    /// (memory/architecture/no-cross-section-ef-joins.md).</summary>
    public Guid UserId { get; init; }

    /// <summary>Normalized IBAN. Null when the member cleared it: the row then records the removal,
    /// so the read never falls back to the legacy profile value.</summary>
    public string? Iban { get; set; }

    public Instant UpdatedAt { get; set; }
}
