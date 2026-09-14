using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// Immutable record of a user's consent to a document version.
/// This table is append-only - no updates or deletes allowed.
/// </summary>
internal sealed class ConsentRecord
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public Guid DocumentVersionId { get; init; }

    public DocumentVersion DocumentVersion { get; set; } = null!;

    public Instant ConsentedAt { get; init; }

    /// <summary>
    /// IP address from which consent was given (for GDPR compliance).
    /// </summary>
    public string IpAddress { get; init; } = string.Empty;

    public string UserAgent { get; init; } = string.Empty;

    /// <summary>
    /// Hash of the document content at time of consent (for verification).
    /// </summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>
    /// Whether the user explicitly checked the consent checkbox.
    /// Must be true for valid consent.
    /// </summary>
    public bool ExplicitConsent { get; init; }
}
