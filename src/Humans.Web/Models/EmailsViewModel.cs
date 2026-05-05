using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

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
    /// When non-null, identifies the email row that is the user's Workspace
    /// canonical identity (Provider=Google + email on the configured Workspace
    /// domain). While set, the view locks Primary and Google radios across the
    /// entire grid: that row's radios stay checked; all other rows' radios are
    /// disabled. The lock releases once the row is removed (which itself
    /// requires unlinking, gated by the more-than-one-email rule).
    /// </summary>
    public Guid? WorkspaceLockedEmailId { get; init; }
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
}
