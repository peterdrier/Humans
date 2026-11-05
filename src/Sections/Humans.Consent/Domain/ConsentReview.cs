using Humans.Consent.Contracts;
using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// The Consent Coordinator review. The rejection state is Users' <c>users.State</c>; this row annotates it.
/// </summary>
internal sealed class ConsentReview
{
    internal const int MaxTextLength = 4000;

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
