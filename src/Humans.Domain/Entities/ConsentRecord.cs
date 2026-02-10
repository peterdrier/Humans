using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Immutable record of a user's consent to a document version.
/// This table is append-only - no updates or deletes allowed.
/// </summary>
public class ConsentRecord
{
    /// <summary>
    /// Unique identifier for the consent record.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the user who gave consent.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Foreign key to the document version being consented to.
    /// </summary>
    public Guid DocumentVersionId { get; init; }

    /// <summary>
    /// Navigation property to the document version.
    /// </summary>
    public DocumentVersion DocumentVersion { get; set; } = null!;

    /// <summary>
    /// When consent was given.
    /// </summary>
    public Instant ConsentedAt { get; init; }

    /// <summary>
    /// IP address from which consent was given (for GDPR compliance).
    /// </summary>
    public string IpAddress { get; init; } = string.Empty;

    /// <summary>
    /// User-Agent string from the consent request.
    /// </summary>
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
