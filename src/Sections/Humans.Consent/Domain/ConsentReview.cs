using Humans.Consent.Contracts;
using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// Replaces the review columns on <c>profiles</c>, which Users keeps unwritten until the
/// <c>consent.profile-review</c> data migration is done in production. The rejection state is Users'
/// <c>users.State</c>; this row annotates it.
/// </summary>
internal sealed class ConsentReview
{
    public Guid UserId { get; init; }

    /// <summary>Null until all required consents are signed.</summary>
    public ConsentCheckStatus? Status { get; set; }

    public Instant? CheckedAt { get; set; }

    public Guid? CheckedByUserId { get; set; }

    public string? Notes { get; set; }

    /// <summary>Annotates; it does not gate Volunteer admission.</summary>
    public bool IsApproved { get; set; }

    public string? RejectionReason { get; set; }

    public Instant? RejectedAt { get; set; }

    public Guid? RejectedByUserId { get; set; }

    public Instant UpdatedAt { get; set; }
}
