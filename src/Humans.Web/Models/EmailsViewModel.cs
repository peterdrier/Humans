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
}
