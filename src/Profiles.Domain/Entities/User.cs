using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Profiles.Domain.Entities;

/// <summary>
/// Custom user entity extending ASP.NET Core Identity.
/// </summary>
public class User : IdentityUser<Guid>
{
    /// <summary>
    /// Display name for the user.
    /// </summary>
    [PersonalData]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Preferred language code (e.g., "en", "es").
    /// Defaults to English.
    /// </summary>
    [PersonalData]
    public string PreferredLanguage { get; set; } = "en";

    /// <summary>
    /// Google profile picture URL.
    /// </summary>
    [PersonalData]
    public string? ProfilePictureUrl { get; set; }

    /// <summary>
    /// When the user account was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the user last logged in.
    /// </summary>
    public Instant? LastLoginAt { get; set; }

    /// <summary>
    /// Navigation property to the member profile.
    /// </summary>
    public Profile? Profile { get; set; }

    /// <summary>
    /// Navigation property to role assignments.
    /// </summary>
    public ICollection<RoleAssignment> RoleAssignments { get; } = new List<RoleAssignment>();

    /// <summary>
    /// Navigation property to consent records.
    /// </summary>
    public ICollection<ConsentRecord> ConsentRecords { get; } = new List<ConsentRecord>();

    /// <summary>
    /// Navigation property to applications.
    /// </summary>
    public ICollection<Application> Applications { get; } = new List<Application>();

    /// <summary>
    /// Navigation property to team memberships.
    /// </summary>
    public ICollection<TeamMember> TeamMemberships { get; } = new List<TeamMember>();

    /// <summary>
    /// User-specified preferred email address for system notifications.
    /// Must be verified before use.
    /// </summary>
    [PersonalData]
    public string? PreferredEmail { get; set; }

    /// <summary>
    /// Whether the preferred email has been verified.
    /// </summary>
    [PersonalData]
    public bool PreferredEmailVerified { get; set; }

    /// <summary>
    /// When the last verification email was sent (for rate limiting).
    /// </summary>
    public Instant? PreferredEmailVerificationSentAt { get; set; }

    /// <summary>
    /// Gets the effective email address for system notifications.
    /// Returns the verified preferred email if available, otherwise the OAuth email.
    /// </summary>
    public string? GetEffectiveEmail() =>
        PreferredEmailVerified && !string.IsNullOrEmpty(PreferredEmail)
            ? PreferredEmail
            : Email;

    /// <summary>
    /// When the last re-consent reminder email was sent (for rate limiting).
    /// </summary>
    public Instant? LastConsentReminderSentAt { get; set; }

    /// <summary>
    /// When the user requested account deletion.
    /// Null if no deletion is pending.
    /// </summary>
    public Instant? DeletionRequestedAt { get; set; }

    /// <summary>
    /// When the account will be permanently deleted.
    /// Set to DeletionRequestedAt + 30 days when a deletion is requested.
    /// </summary>
    public Instant? DeletionScheduledFor { get; set; }

    /// <summary>
    /// Whether a deletion request is pending.
    /// </summary>
    public bool IsDeletionPending => DeletionRequestedAt.HasValue;
}
