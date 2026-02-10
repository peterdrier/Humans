using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

public class PreferredEmailViewModel
{
    /// <summary>
    /// The user's OAuth email (read-only, for display).
    /// </summary>
    public string OAuthEmail { get; set; } = string.Empty;

    /// <summary>
    /// The current preferred email address.
    /// </summary>
    public string? CurrentPreferredEmail { get; set; }

    /// <summary>
    /// Whether the current preferred email is verified.
    /// </summary>
    public bool IsVerified { get; set; }

    /// <summary>
    /// Whether a verification email is pending (sent but not yet verified).
    /// </summary>
    public bool IsPendingVerification { get; set; }

    /// <summary>
    /// Whether the user can request a new verification email (rate limit check).
    /// </summary>
    public bool CanResendVerification { get; set; }

    /// <summary>
    /// Minutes until the user can request a new verification email.
    /// </summary>
    public int MinutesUntilResend { get; set; }

    /// <summary>
    /// The new email address to set (for form submission).
    /// </summary>
    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    [StringLength(256)]
    [Display(Name = "New Email Address")]
    public string? NewEmail { get; set; }
}
