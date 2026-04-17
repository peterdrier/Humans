using Microsoft.AspNetCore.Identity;
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

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
    /// Earliest date the deletion can be processed (event hold for ticket holders).
    /// When set, ProcessAccountDeletionsJob will not process until this date has passed.
    /// </summary>
    public Instant? DeletionEligibleAfter { get; set; }

    /// <summary>
    /// Whether a deletion request is pending.
    /// </summary>
    public bool IsDeletionPending => DeletionRequestedAt.HasValue;

    /// <summary>
    /// Whether the user has unsubscribed from campaign emails.
    /// </summary>
    public bool UnsubscribedFromCampaigns { get; set; }

    /// <summary>
    /// Token for personal iCal feed URL. Regeneratable.
    /// </summary>
    public Guid? ICalToken { get; set; }

    /// <summary>
    /// Whether to suppress email notifications for schedule changes.
    /// </summary>
    public bool SuppressScheduleChangeEmails { get; set; }

    /// <summary>
    /// When the last magic link login email was sent (for rate limiting).
    /// </summary>
    public Instant? MagicLinkSentAt { get; set; }

    /// <summary>
    /// Preferred email for Google services (Groups, Drive).
    /// When set, this email is used instead of the OAuth email for Google resource sync.
    /// Automatically set to @nobodies.team email when provisioned or linked.
    /// </summary>
    [PersonalData]
    public string? GoogleEmail { get; set; }

    /// <summary>
    /// Status of the user's Google email for sync operations.
    /// Set to Rejected when a permanent Google API error occurs; reset to Unknown on email change.
    /// </summary>
    public GoogleEmailStatus GoogleEmailStatus { get; set; } = GoogleEmailStatus.Unknown;

    /// <summary>
    /// Gets the email address used for Google services (Groups, Drive permissions).
    /// Returns GoogleEmail if set, otherwise falls back to the OAuth email.
    /// Does NOT require UserEmails to be loaded.
    /// </summary>
    public string? GetGoogleServiceEmail() => GoogleEmail ?? Email;

    /// <summary>
    /// Where this user was imported from (null for self-registered users).
    /// </summary>
    public ContactSource? ContactSource { get; set; }

    /// <summary>
    /// ID in the external source system (e.g., MailerLite subscriber ID).
    /// </summary>
    public string? ExternalSourceId { get; set; }

    /// <summary>
    /// Navigation property to event participation records.
    /// </summary>
    public ICollection<EventParticipation> EventParticipations { get; } = new List<EventParticipation>();
}
