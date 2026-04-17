using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// An email address associated with a user account.
/// Supports OAuth login email, additional verified emails, notification targeting, and profile visibility.
/// </summary>
public class UserEmail
{
    /// <summary>
    /// Unique identifier for this email record.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the user.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// The email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Whether this email has been verified.
    /// </summary>
    public bool IsVerified { get; set; }

    /// <summary>
    /// Whether this is the OAuth login email (cannot be deleted).
    /// </summary>
    public bool IsOAuth { get; set; }

    /// <summary>
    /// Whether this email is the notification target (exactly one per user must be true).
    /// </summary>
    public bool IsNotificationTarget { get; set; }

    /// <summary>
    /// Profile visibility for this email. Null means hidden from profile.
    /// </summary>
    public ContactFieldVisibility? Visibility { get; set; }

    /// <summary>
    /// When the last verification email was sent (for rate limiting).
    /// </summary>
    public Instant? VerificationSentAt { get; set; }

    /// <summary>
    /// Display order for sorting emails in the UI.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// When this email record was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this email record was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }
}
