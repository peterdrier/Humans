using System.ComponentModel.DataAnnotations;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Models;

internal sealed class EmailsViewModel
{
    public IReadOnlyList<EmailRowViewModel> Emails { get; set; } = [];

    [EmailAddress(ErrorMessage = "Validation_EmailAddress")]
    [StringLength(256, ErrorMessage = "Validation_MaxLength")]
    [Display(Name = "New Email Address")]
    public string? NewEmail { get; set; }

    /// <summary>
    /// Account state permits inserting and the verification rate limit permits sending.
    /// Administrators bypass the rate limit, but cannot insert into terminal accounts.
    /// </summary>
    public bool CanAddEmail { get; set; } = true;

    public int MinutesUntilResend { get; set; }

    /// <summary>The user being acted on: the target in admin contexts, not the actor.</summary>
    public Guid TargetUserId { get; set; }

    public string? TargetDisplayName { get; set; }

    public bool IsAdminContext { get; set; }

    /// <summary>
    /// The raw Identity <c>Email</c> column — the silent fallback behind the <c>User.Email</c>
    /// override. Full Admin on another user only; null otherwise.
    /// </summary>
    public string? LegacyIdentityEmailColumn { get; set; }

    /// <summary>Admin-only diagnostic: the target's cached <c>UserInfo</c>. Null otherwise.</summary>
    public UserInfo? TargetUserInfo { get; init; }

    /// <summary>
    /// The Workspace identity row. While set, the grid's Primary and Google radios are locked to it;
    /// removing the row requires unlinking first.
    /// </summary>
    public Guid? WorkspaceLockedEmailId { get; init; }

    /// <summary>Admin-only <c>AspNetUserLogins</c> snapshot. Empty in self contexts.</summary>
    public IReadOnlyList<ExternalLoginRowViewModel> ExternalLogins { get; init; } =
        [];

    /// <summary>Admin-only raw UserEmail rows. Empty in self contexts.</summary>
    public IReadOnlyList<UserEmailRowSnapshot> RawUserEmails { get; init; } =
        [];

    /// <summary>
    /// The user's linked OAuth providers, from <c>AspNetUserLogins</c>. Self contexts only; admins
    /// get <see cref="ExternalLogins"/>.
    /// </summary>
    public IReadOnlyList<LinkedOAuthAccountViewModel> LinkedAccounts { get; init; } =
        [];
}

/// <summary>
/// One linked OAuth provider, keyed off <c>AspNetUserLogins</c>; the matching <c>UserEmail</c> row,
/// when present, supplies the linked-on time and the unlink row id.
/// </summary>
internal sealed class LinkedOAuthAccountViewModel
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderKey { get; init; } = string.Empty;
    public string? ProviderDisplayName { get; init; }

    /// <summary>First 8 hex chars of SHA-256(ProviderKey), so a screenshot never leaks the OIDC <c>sub</c>.</summary>
    public string ProviderKeyHash { get; init; } = string.Empty;

    /// <summary>
    /// The <c>UserEmail</c> row tagged with this login; null for an orphan login. Unlink goes
    /// through it so <c>UserEmailService.UnlinkAsync</c> keeps both stores in sync.
    /// </summary>
    public Guid? MatchingUserEmailId { get; init; }

    public string? Email { get; init; }

    /// <summary>The matching row's CreatedAt; <c>AspNetUserLogins</c> has no timestamp.</summary>
    public Instant? LinkedAt { get; init; }

    /// <summary>
    /// False when unlinking would leave no way to sign in (no verified email for magic link).
    /// The controller re-validates server-side.
    /// </summary>
    public bool CanUnlink { get; init; }
}

/// <summary>One AspNetUserLogins row for the admin diagnostic.</summary>
internal sealed class ExternalLoginRowViewModel
{
    public string LoginProvider { get; init; } = string.Empty;
    /// <summary>SHA256 prefix of the OIDC `sub`. Shown rather than the raw
    /// key so the page can be copied to chat without leaking the identifier.</summary>
    public string ProviderKeyHash { get; init; } = string.Empty;
    public string? ProviderDisplayName { get; init; }

    /// <summary>
    /// No <c>UserEmail</c> row carries this login's tag. Self-heals on the next OAuth sign-in.
    /// </summary>
    public bool HasOrphanLogin { get; init; }
}

internal sealed class EmailRowViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public bool IsPrimary { get; set; }
    public ContactFieldVisibility? Visibility { get; set; }
    public bool IsPendingVerification { get; set; }
    public bool IsMergePending { get; set; }
    public bool IsNobodiesTeamDomain { get; set; }

    /// <summary>
    /// Linked to the user's event ticket: the grid hides Delete and the service re-validates.
    /// </summary>
    public bool IsTicketLinked { get; set; }

    /// <summary>Sign-in provider, e.g. "Google"; provider rows show Unlink, plain rows Delete.</summary>
    public string? Provider { get; set; }

    /// <summary>Tagged with a provider identity the user has no <c>AspNetUserLogins</c> row for.</summary>
    public bool HasOrphanProviderTag { get; init; }
}
