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
    /// First verified <see cref="UserEmail"/>, ordered by
    /// <see cref="UserEmail.IsPrimary"/> desc; falls back to
    /// <c>base.Email</c> when no UserEmails are loaded (test fixtures,
    /// post-anonymization reads). Requires <see cref="UserEmails"/> to be
    /// loaded for production reads.
    /// </summary>
    /// <remarks>
    /// SILENT-FALLBACK FOOTGUN: when <see cref="UserEmails"/> is not loaded
    /// (the navigation collection is empty), the getter returns
    /// <c>base.Email</c> — the Identity column. After PR 2 of the
    /// email-identity-decoupling spec, <c>base.Email</c> is <c>null</c> for
    /// users created post-PR 1 (writes were stopped); for pre-PR 1 users it
    /// still holds the legacy column value. Either result is wrong when the
    /// caller wanted the canonical UserEmails-derived address. Always
    /// <c>.Include(u =&gt; u.UserEmails)</c> when loading a User whose
    /// <c>Email</c> will be read. Repository methods that intentionally skip
    /// the include (e.g., projections that don't read Email) are responsible
    /// for documenting that constraint locally — there is no runtime warning
    /// when the include is missing.
    /// </remarks>
    public override string? Email
    {
        get
        {
            if (UserEmails.Count == 0)
                return base.Email;

            return UserEmails
                .Where(e => e.IsVerified)
                .OrderByDescending(e => e.IsPrimary)
                .Select(e => e.Email)
                .FirstOrDefault() ?? base.Email;
        }
        set => base.Email = value;
    }

    /// <inheritdoc />
    public override string? NormalizedEmail
    {
        get => Email?.ToUpperInvariant();
        set => base.NormalizedEmail = value;
    }

    /// <inheritdoc />
    public override bool EmailConfirmed
    {
        get => UserEmails.Any(e => e.IsVerified) || base.EmailConfirmed;
        set => base.EmailConfirmed = value;
    }

    /// <summary>
    /// Anchored to <see cref="IdentityUser{TKey}.Id"/> so Identity's username
    /// uniqueness validator always sees a non-empty unique value without
    /// callers having to populate it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CALLER CONTRACT: <see cref="IdentityUser{TKey}.Id"/> MUST be assigned
    /// <em>before</em> any code path reads <c>UserName</c>, including
    /// <c>UserManager.CreateAsync</c>'s username-uniqueness validator. EF
    /// assigns <c>Id</c> at <c>SaveChanges</c>, which is too late — the
    /// validator runs first, sees <c>base.UserName == null</c>, and the getter
    /// returns <c>Guid.Empty.ToString()</c>. Multiple users created in one
    /// run will then collide on <c>"00000000-0000-0000-0000-000000000000"</c>
    /// and <c>CreateAsync</c> fails on the second user.
    /// </para>
    /// <para>
    /// Always set the Id explicitly:
    /// <code>
    /// var userId = Guid.NewGuid();
    /// var user = new User { Id = userId, ... };
    /// await _userManager.CreateAsync(user);
    /// </code>
    /// </para>
    /// </remarks>
    public override string? UserName
    {
        get => base.UserName ?? Id.ToString();
        set => base.UserName = value;
    }

    /// <inheritdoc />
    public override string? NormalizedUserName
    {
        get => base.NormalizedUserName ?? Id.ToString().ToUpperInvariant();
        set => base.NormalizedUserName = value;
    }

    /// <summary>
    /// Effective email for system notifications. Requires
    /// <see cref="UserEmails"/> to be loaded.
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
    /// Status of the user's Google email for sync operations.
    /// Set to Rejected when a permanent Google API error occurs; reset to Unknown on email change.
    /// </summary>
    public GoogleEmailStatus GoogleEmailStatus { get; set; } = GoogleEmailStatus.Unknown;

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
