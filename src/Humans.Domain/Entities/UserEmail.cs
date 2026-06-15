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
    ///
    /// MUTATION: see <c>memory/architecture/email-mutation-paths.md</c>. The only
    /// path that rewrites this field on an existing row is
    /// <c>UserEmailService.ReconcileOAuthIdentityAsync</c>, matched on
    /// <see cref="Provider"/>+<see cref="ProviderKey"/>, called only by the
    /// OAuth sign-in callback in <c>AccountController</c>. Admin flows, sync
    /// jobs, and profile UI never rewrite an existing row's address — renames
    /// self-heal on the user's next OAuth sign-in. Rows can be added or removed,
    /// not edited.
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
    /// across Google Workspace email renames. <see cref="Provider"/>+
    /// <c>ProviderKey</c> is the only legitimate match key for rewriting
    /// <see cref="Email"/> — see <c>memory/architecture/email-mutation-paths.md</c>.
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
    /// Google Workspace sync status for THIS address. <see cref="GoogleEmailStatus.Rejected"/>
    /// is set when Google permanently rejects this email (HTTP 400/403, "no Google account")
    /// while granting Drive/Group access; sync is then suppressed for the address until it
    /// changes. Because the status lives on the address Google actually rejected — not on the
    /// user — selecting a different <see cref="IsGoogle"/> address (a fresh row, status
    /// <see cref="GoogleEmailStatus.Unknown"/>) naturally resumes sync. Replaces the deprecated
    /// user-level <c>User.GoogleEmailStatus</c> (nobodies-collective/Humans#687).
    /// </summary>
    public GoogleEmailStatus GoogleEmailStatus { get; set; } = GoogleEmailStatus.Unknown;

    /// <summary>
    /// True when this row is the canonical recipient for system notifications
    /// to this user. Exactly-one-true-per-UserId is service-enforced inside
    /// UserEmailService — no DB partial unique index per
    /// feedback_db_enforcement_minimal.
    /// </summary>
    public bool IsPrimary { get; set; }

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
