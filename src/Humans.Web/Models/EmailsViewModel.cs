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
}

/// <summary>
/// A single email row in the Manage Emails page.
/// </summary>
public class EmailRowViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public bool IsOAuth { get; set; }
    public bool IsNotificationTarget { get; set; }
    public ContactFieldVisibility? Visibility { get; set; }
    public bool IsPendingVerification { get; set; }
    public bool IsMergePending { get; set; }
    public bool IsGoogleServiceEmail { get; set; }
    public bool IsNobodiesTeamDomain { get; set; }
}
