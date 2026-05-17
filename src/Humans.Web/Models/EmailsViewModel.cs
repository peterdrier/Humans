using System.ComponentModel.DataAnnotations;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// View model for the Manage Emails page.
/// </summary>
public class EmailsViewModel
{
    /// <summary>
    /// All email addresses for the user.
    /// </summary>
    public IReadOnlyList<EmailRowViewModel> Emails { get; set; } = [];

    /// <summary>
    /// New email address to add (form input).
    /// </summary>
    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    [StringLength(256)]
    [Display(Name = "New Email Address")]
    public string? NewEmail { get; set; }

    /// <summary>
    /// Whether the user can send a new verification email (rate limit check).
    /// </summary>
    public bool CanAddEmail { get; set; } = true;

    /// <summary>
    /// Minutes until the user can request a new verification email.
    /// </summary>
    public int MinutesUntilResend { get; set; }

    /// <summary>
    /// The email currently selected for Google services (Groups, Drive).
    /// Null means OAuth email is used (default).
    /// </summary>
    public string? GoogleServiceEmail { get; set; }

    /// <summary>
    /// Whether the user has a verified @nobodies.team email (which auto-locks Google preference).
    /// </summary>
    public bool HasNobodiesTeamEmail { get; set; }

    /// <summary>
    /// Status of the Google email for sync operations.
    /// </summary>
    public GoogleEmailStatus GoogleEmailStatus { get; set; }

    /// <summary>
    /// The user this grid is acting on. For self contexts this is the current user;
    /// for admin contexts this is the target user (route param), not the actor.
    /// </summary>
    public Guid TargetUserId { get; set; }

    /// <summary>
    /// Display name of the target user. Used in the admin context banner so
    /// it reads "Managing emails for {name}" rather than a guid fragment.
    /// </summary>
    public string? TargetDisplayName { get; set; }

    /// <summary>
    /// True when this view is being rendered against another user's grid by an admin.
    /// </summary>
    public bool IsAdminContext { get; set; }

    /// <summary>
    /// Raw legacy AspNetIdentity <c>Email</c> column value for the target user.
    /// Populated only when a full Admin views another user via the admin route —
    /// the column is the silent fallback behind the
    /// <c>User.Email</c>-from-<c>UserEmails</c> override and is otherwise
    /// invisible. Diagnostic surface for the email-identity-decoupling
    /// rollout. Null in self contexts and for non-Admin actors.
    /// </summary>
    public string? LegacyIdentityEmailColumn { get; set; }

    /// <summary>
    /// When non-null, identifies the email row that is the user's Workspace
    /// canonical identity (Provider=Google + email on the configured Workspace
    /// domain). While set, the view locks Primary and Google radios across the
    /// entire grid: that row's radios stay checked; all other rows' radios are
    /// disabled. The lock releases once the row is removed (which itself
    /// requires unlinking, gated by the more-than-one-email rule).
    /// </summary>
    public Guid? WorkspaceLockedEmailId { get; init; }

    /// <summary>
    /// Issue nobodies-collective/Humans#697: admin-only `AspNetUserLogins`
    /// snapshot shown alongside the `UserEmail` grid. Empty in self contexts.
    /// </summary>
    public IReadOnlyList<ExternalLoginRowViewModel> ExternalLogins { get; init; } =
        [];

    /// <summary>
    /// Admin-only raw UserEmail row snapshots for the target user — every
    /// exposed metadata column, no formatting. Diagnostic surface for reading
    /// the stored row shape directly. Empty in self contexts.
    /// </summary>
    public IReadOnlyList<UserEmailRowSnapshot> RawUserEmails { get; init; } =
        [];

    /// <summary>
    /// Issue nobodies-collective/Humans#731: user-facing dashboard of OAuth
    /// providers linked to the current user, keyed off
    /// <c>AspNetUserLogins</c>. Populated only in self contexts; empty in
    /// admin contexts (admins use the diagnostic
    /// <see cref="ExternalLogins"/> table instead).
    /// </summary>
    public IReadOnlyList<LinkedOAuthAccountViewModel> LinkedAccounts { get; init; } =
        [];
}

/// <summary>
/// One linked OAuth provider on the user-facing Linked Accounts dashboard
/// (issue nobodies-collective/Humans#731). Keyed off the authoritative
/// <c>AspNetUserLogins</c> store; the matching <c>UserEmail</c> row (when
/// present) supplies the linked-on timestamp and the row id used by the
/// unlink endpoint.
/// </summary>
public class LinkedOAuthAccountViewModel
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderKey { get; init; } = string.Empty;
    public string? ProviderDisplayName { get; init; }

    /// <summary>
    /// First 8 hex chars of SHA-256(ProviderKey). Shown rather than the raw
    /// OIDC <c>sub</c> so the page can be screenshotted without leaking the
    /// stable provider identifier.
    /// </summary>
    public string ProviderKeyHash { get; init; } = string.Empty;

    /// <summary>
    /// When non-null, the <c>UserEmail</c> row tagged with this
    /// <c>(Provider, ProviderKey)</c> pair. Null when the AspNetUserLogins
    /// row is orphan (no matching UserEmail tag — admin-diagnostic edge
    /// case). The row id is needed to route the unlink action through
    /// <c>UserEmailService.UnlinkAsync</c>, which keeps the two stores in
    /// sync.
    /// </summary>
    public Guid? MatchingUserEmailId { get; init; }

    /// <summary>
    /// The email address on the matching <c>UserEmail</c> row, when present.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// CreatedAt of the matching <c>UserEmail</c> row, when present.
    /// Approximates the linked-on timestamp (AspNetUserLogins itself has no
    /// timestamp column).
    /// </summary>
    public Instant? LinkedAt { get; init; }

    /// <summary>
    /// False when unlinking this provider would leave the user with no way
    /// to sign in — i.e. no remaining verified email row (which magic-link
    /// would otherwise grant). UI hides the Unlink button when false; the
    /// controller re-validates the invariant server-side.
    /// </summary>
    public bool CanUnlink { get; init; }
}

/// <summary>
/// One AspNetUserLogins row for the per-user admin diagnostic (issue
/// nobodies-collective/Humans#697).
/// </summary>
public class ExternalLoginRowViewModel
{
    public string LoginProvider { get; init; } = string.Empty;
    /// <summary>SHA256 prefix of the OIDC `sub`. Shown rather than the raw
    /// key so the page can be copied to chat without leaking the identifier.</summary>
    public string ProviderKeyHash { get; init; } = string.Empty;
    public string? ProviderDisplayName { get; init; }

    /// <summary>
    /// True when this AspNetUserLogins row has no matching <c>UserEmail</c>
    /// row tagged with the same <c>(Provider, ProviderKey)</c> for this user
    /// — i.e. the OAuth identity is authoritative but the per-row tag is
    /// missing. Self-heals on the user's next OAuth sign-in via
    /// <c>ReconcileOAuthIdentityAsync</c>.
    /// </summary>
    public bool HasOrphanLogin { get; init; }
}

/// <summary>
/// A single email row in the Manage Emails page.
/// </summary>
public class EmailRowViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public bool IsGoogle { get; set; }
    public bool IsPrimary { get; set; }
    public ContactFieldVisibility? Visibility { get; set; }
    public bool IsPendingVerification { get; set; }
    public bool IsMergePending { get; set; }
    public bool IsNobodiesTeamDomain { get; set; }

    /// <summary>
    /// Sign-in provider attached to this email row, e.g. "Google". Null when
    /// the row is a plain (provider-free) email. Drives the contextual action
    /// button: provider-attached rows show Unlink; plain rows show Delete.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Issue nobodies-collective/Humans#697: true when this row carries a
    /// <c>(Provider, ProviderKey)</c> tag but the user has no matching
    /// <c>AspNetUserLogins</c> row for the same identity. Surfaces the
    /// "row tagged but the authoritative OAuth identity is gone" disagreement
    /// in the per-user admin diagnostic.
    /// </summary>
    public bool HasOrphanProviderTag { get; init; }
}
