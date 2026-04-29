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
    /// Navigation property to email addresses.
    /// </summary>
    public ICollection<UserEmail> UserEmails { get; } = new List<UserEmail>();

    /// <summary>
    /// Email is sourced from <see cref="UserEmails"/> per the
    /// email-identity-decoupling spec PR 2: returns the first verified row
    /// ordered by <see cref="UserEmail.IsNotificationTarget"/> descending,
    /// falling back to the in-memory <c>base.Email</c> value when the user
    /// has no UserEmail rows (e.g., in-memory test fixtures, post-anonymization
    /// reads). The EF column is <c>Ignore()</c>'d so persistence flows entirely
    /// through <c>UserEmails</c>.
    /// <para>
    /// Setter delegates to <c>base.Email</c> for in-memory continuity (tests +
    /// any transient assignments), but should not be called from production
    /// code outside <c>HumansUserStore</c>. The
    /// <c>IdentityColumnWriteRestrictionsTests</c> architecture test enforces
    /// this in <c>Humans.Application</c> + <c>Humans.Web</c>; modify
    /// <see cref="UserEmail"/> rows via <c>IUserEmailService</c> for the
    /// canonical write path.
    /// </para>
    /// </summary>
    public override string? Email
    {
        get
        {
            var fromUserEmails = UserEmails
                .Where(e => e.IsVerified)
                .OrderByDescending(e => e.IsNotificationTarget)
                .Select(e => e.Email)
                .FirstOrDefault();
            return fromUserEmails ?? base.Email;
        }
        set => base.Email = value;
    }

    /// <summary>
    /// Derived from <see cref="Email"/>; falls back to <c>base.NormalizedEmail</c>
    /// for in-memory continuity. Setter delegates to base.
    /// </summary>
    public override string? NormalizedEmail
    {
        get => Email?.ToUpperInvariant() ?? base.NormalizedEmail;
        set => base.NormalizedEmail = value;
    }

    /// <summary>
    /// True when the user has at least one verified <see cref="UserEmail"/>,
    /// or when <c>base.EmailConfirmed</c> was set (e.g., test fixture).
    /// Setter delegates to base.
    /// </summary>
    public override bool EmailConfirmed
    {
        get => UserEmails.Any(e => e.IsVerified) || base.EmailConfirmed;
        set => base.EmailConfirmed = value;
    }

    /// <summary>
    /// Identity needs a unique non-empty UserName for validator + uniqueness
    /// checks; we anchor it to <see cref="IdentityUser{TKey}.Id"/>. Setter
    /// silently delegates to base (Identity's <c>SetUserNameAsync</c> contract
    /// requires the call to succeed) but the EF column is <c>Ignore()</c>'d,
    /// so the value never round-trips to the database. Getter returns the
    /// last-set value if any, falling back to <c>Id.ToString()</c>.
    /// </summary>
    public override string? UserName
    {
        get => base.UserName ?? Id.ToString();
        set => base.UserName = value;
    }

    /// <summary>
    /// Mirrors <see cref="UserName"/> normalization. Same silent-set semantics
    /// — the EF column is dropped, so persistence is irrelevant; the value is
    /// only used for in-memory uniqueness checks routed through
    /// <c>HumansUserStore.FindByNameAsync</c>.
    /// </summary>
    public override string? NormalizedUserName
    {
        get => base.NormalizedUserName ?? Id.ToString().ToUpperInvariant();
        set => base.NormalizedUserName = value;
    }

    /// <summary>
    /// Gets the effective email address for system notifications. Identical
    /// to <see cref="Email"/> after PR 2 of the email-identity-decoupling
    /// spec — the override on <see cref="Email"/> already returns the first
    /// verified row by IsNotificationTarget desc. Kept as a separate method
    /// for callers that want the explicit "this is the notify-this-human
    /// address" intent. Requires <see cref="UserEmails"/> to be loaded.
    /// </summary>
    public string? GetEffectiveEmail() => Email;

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
    /// Navigation property to communication preferences.
    /// </summary>
    public ICollection<CommunicationPreference> CommunicationPreferences { get; } = new List<CommunicationPreference>();

    /// <summary>
    /// Navigation property to event participation records.
    /// </summary>
    public ICollection<EventParticipation> EventParticipations { get; } = new List<EventParticipation>();
}
