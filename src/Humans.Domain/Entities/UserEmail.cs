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
    /// OAuth provider that owns this email row, when the user is signed in via OIDC.
    /// "Google" today; future "Apple", "Microsoft". Null when no OAuth identity is
    /// linked to this row. Single-row-per-(Provider, ProviderKey) is service-enforced
    /// inside <c>UserEmailService</c> per <c>feedback_db_enforcement_minimal</c>.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// OAuth subject/key (OIDC <c>sub</c>) for the linked OAuth identity. Stable
    /// across Google Workspace email renames — the OAuth callback compares the
    /// row's Email to the incoming claim email and updates the row when they
    /// diverge.
    /// </summary>
    public string? ProviderKey { get; set; }

    /// <summary>
    /// True when this is the user's canonical Google Workspace identity (used by
    /// Google sync and Workspace admin operations). User-controlled in the
    /// Profile email grid (PR 4); never auto-derived. At-most-one-true-per-UserId
    /// is service-enforced inside <c>UserEmailService</c> per
    /// <c>feedback_db_enforcement_minimal</c>.
    /// </summary>
    public bool IsGoogle { get; set; }

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
    /// When this email record was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this email record was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }
}
