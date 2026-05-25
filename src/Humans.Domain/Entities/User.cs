using Microsoft.AspNetCore.Identity;
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Custom user entity extending ASP.NET Core Identity.
/// </summary>
public class User : IdentityUser<Guid>
{
    /// <summary>Legacy Identity column — fallback only. Render via UserInfo.BurnerName / &lt;vc:human&gt;.</summary>
    [PersonalData]
    [Obsolete("Rendering callers must use UserInfo.BurnerName / <vc:human> per memory/architecture/burnername-is-the-display-name.md. Legitimate consumers: creation-time BurnerName fallback, repository merge/purge/delete labels, GDPR export, debug screens.", DiagnosticId = "HUM_USER_DISPLAYNAME", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/691")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Preferred language code (e.g., "en", "es"). Defaults to English.</summary>
    [PersonalData]
    public string PreferredLanguage { get; set; } = "en";

    /// <summary>Google profile picture URL.</summary>
    [PersonalData]
    public string? ProfilePictureUrl { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant? LastLoginAt { get; set; }

    /// <summary>The only cross-domain nav on User; required by the Email override. See #635.</summary>
    public ICollection<UserEmail> UserEmails { get; } = new List<UserEmail>();

    /// <summary>
    /// First verified UserEmail (IsPrimary desc); falls back to base.Email when UserEmails isn't loaded.
    /// Always .Include(u => u.UserEmails) when reading Email. Vestigial Identity field — never set directly.
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

    /// <summary>Raw legacy Identity Email column — diagnostic only. Application code should read <see cref="Email"/>.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? IdentityEmailColumn => base.Email;

    /// <inheritdoc />
#pragma warning disable CS0809 // Obsolete override of non-obsolete base — intentional.
    [Obsolete("NormalizedEmail is shadow-populated by Identity. Use User.Email or IUserEmailRepository for canonical email lookup.", DiagnosticId = "HUM_USER_NORMALIZEDEMAIL", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/635")]
    [Architecture.ExpiresOn("2026-06-01", reason: "Issue #635 — Identity shadow column; application reads should go through User.Email / IUserEmailRepository.")]
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
    /// Falls back to Id so Identity's uniqueness validator always sees a value.
    /// CALLER CONTRACT: assign Id before CreateAsync — EF assigns at SaveChanges,
    /// which is too late and collides every new user on Guid.Empty.
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

    /// <summary>When the last re-consent reminder email was sent (rate limiting).</summary>
    public Instant? LastConsentReminderSentAt { get; set; }

    /// <summary>When the user requested account deletion; null if none pending.</summary>
    public Instant? DeletionRequestedAt { get; set; }

    /// <summary>When the account will be permanently deleted (DeletionRequestedAt + 30d).</summary>
    public Instant? DeletionScheduledFor { get; set; }

    /// <summary>Earliest date deletion can process (event hold for ticket holders).</summary>
    public Instant? DeletionEligibleAfter { get; set; }

    public bool IsDeletionPending => DeletionRequestedAt.HasValue;

    public bool UnsubscribedFromCampaigns { get; set; }

    /// <summary>Regeneratable token for the personal iCal feed URL.</summary>
    public Guid? ICalToken { get; set; }

    public bool SuppressScheduleChangeEmails { get; set; }

    /// <summary>When the last magic link login email was sent (rate limiting).</summary>
    public Instant? MagicLinkSentAt { get; set; }

    /// <summary>Rejected on permanent Google API error; reset to Unknown on email change.</summary>
    public GoogleEmailStatus GoogleEmailStatus { get; set; } = GoogleEmailStatus.Unknown;

    /// <summary>Source of this user; null for self-registered.</summary>
    public ContactSource? ContactSource { get; set; }

    /// <summary>ID in the external source system (e.g., MailerLite subscriber ID).</summary>
    public string? ExternalSourceId { get; set; }

    public ICollection<EventParticipation> EventParticipations { get; } = new List<EventParticipation>();

    /// <summary>Tombstone — folded into MergedToUserId by AccountMergeService.AcceptAsync. Cannot sign in.</summary>
    public Guid? MergedToUserId { get; set; }

    /// <summary>When the merge tombstone was applied; null while live.</summary>
    public Instant? MergedAt { get; set; }
}
