using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Humans.Users.Contracts;

public class User : IdentityUser<Guid>
{
    /// <summary>Legacy Identity column — fallback only. Render via UserInfo.BurnerName / &lt;vc:human&gt;.</summary>
    [PersonalData]
    [Obsolete("Rendering callers must use UserInfo.BurnerName / <vc:human> per memory/architecture/burnername-is-the-display-name.md. Legitimate consumers: creation-time BurnerName fallback, repository merge/purge/delete labels, GDPR export, debug screens.", DiagnosticId = "HUM_USER_DISPLAYNAME", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/691")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The name we render, and the sole source for <c>UserInfo.BurnerName</c>. Dual-written from Profile; null until
    /// backfilled.
    /// </summary>
    [PersonalData]
    public string? BurnerName { get; set; }

    /// <summary>Dual-written from Profile; null until backfilled.</summary>
    [PersonalData]
    public string? FirstName { get; set; }

    /// <summary>Dual-written from Profile; null until backfilled.</summary>
    [PersonalData]
    public string? LastName { get; set; }

    /// <summary>Defaults to English.</summary>
    [PersonalData]
    public string PreferredLanguage { get; set; } = "en";

    [PersonalData]
    public string? ProfilePictureUrl { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant? LastLoginAt { get; set; }

    /// <summary>The only cross-domain nav on User; the Email override needs it.</summary>
    public ICollection<UserEmail> UserEmails { get; } = new List<UserEmail>();

    /// <summary>
    /// First verified row, primary first; falls back to base.Email when UserEmails isn't loaded, so always
    /// <c>.Include(u => u.UserEmails)</c> when reading it. Never set directly.
    /// </summary>
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

    /// <summary>Raw legacy Identity column, diagnostic only. Read <see cref="Email"/>.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? IdentityEmailColumn => base.Email;

    /// <inheritdoc />
#pragma warning disable CS0809 // Obsolete override of non-obsolete base: intentional.
    [Obsolete("NormalizedEmail is shadow-populated by Identity. Use User.Email or IUserEmailService for canonical email lookup.", DiagnosticId = "HUM_USER_NORMALIZEDEMAIL", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/635")]
    public override string? NormalizedEmail
    {
        get => Email?.ToUpperInvariant();
        set => base.NormalizedEmail = value;
    }
#pragma warning restore CS0809

    /// <inheritdoc />
    public override bool EmailConfirmed
    {
        get => UserEmails.Any(e => e.IsVerified) || base.EmailConfirmed;
        set => base.EmailConfirmed = value;
    }

    /// <summary>
    /// Falls back to Id so Identity's uniqueness validator always sees a value. Assign Id before CreateAsync: EF
    /// assigns it at SaveChanges, too late, and every new user collides on Guid.Empty.
    /// </summary>
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
    /// Frozen: the stamp lives in Consent's <c>consent_reminders</c>. Written only to clear it on GDPR erasure;
    /// the column goes with its separate drop migration.
    /// </summary>
    public Instant? LastConsentReminderSentAt { get; set; }

    public Instant? DeletionRequestedAt { get; set; }

    /// <summary>DeletionRequestedAt + 30 days.</summary>
    public Instant? DeletionScheduledFor { get; set; }

    /// <summary>Event hold for ticket holders: deletion waits past it too.</summary>
    public Instant? DeletionEligibleAfter { get; set; }

    public bool IsDeletionPending => DeletionRequestedAt.HasValue;

    [Obsolete("No reader or writer; retained for the DB column until a drop migration (peterdrier/Humans#2156).")]
    public bool UnsubscribedFromCampaigns { get; set; }

    [Obsolete("No reader or writer; retained for the DB column until a drop migration (peterdrier/Humans#2156).")]
    public bool SuppressScheduleChangeEmails { get; set; }

    /// <summary>Null for self-registered users.</summary>
    public ContactSource? ContactSource { get; set; }

    /// <summary>For example the MailerLite subscriber id.</summary>
    public string? ExternalSourceId { get; set; }

    public ICollection<EventParticipation> EventParticipations { get; } = new List<EventParticipation>();

    /// <summary>Set when this row is a merge tombstone. Cannot sign in.</summary>
    public Guid? MergedToUserId { get; set; }

    public Instant? MergedAt { get; set; }

    /// <summary>The single source of truth for access. Written at each transition; never derived on read.</summary>
    public UserState State { get; set; }
}
