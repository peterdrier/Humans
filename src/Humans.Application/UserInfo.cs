using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application;

/// <summary>
/// Compact projection of a <see cref="UserEmail"/> row carried inside
/// <see cref="UserInfo"/>.
/// </summary>
public sealed record UserEmailInfo(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsPrimary,
    bool IsGoogle,
    string? Provider,
    string? ProviderKey,
    ContactFieldVisibility? Visibility);

/// <summary>
/// Compact projection of a <see cref="ContactField"/> row carried inside
/// <see cref="ProfileInfo"/>.
/// </summary>
public sealed record ContactFieldInfo(
    Guid Id,
    ContactFieldType FieldType,
    string? CustomLabel,
    string Value,
    ContactFieldVisibility Visibility,
    int DisplayOrder);

/// <summary>
/// Compact projection of a <see cref="ProfileLanguage"/> row.
/// </summary>
public sealed record ProfileLanguageInfo(
    Guid Id,
    string LanguageCode,
    LanguageProficiency Proficiency);

/// <summary>
/// Compact projection of a <see cref="VolunteerHistoryEntry"/> row.
/// </summary>
public sealed record VolunteerHistoryInfo(
    Guid Id,
    LocalDate Date,
    string EventName,
    string? Description);

/// <summary>
/// Compact projection of a <see cref="CommunicationPreference"/> row.
/// </summary>
public sealed record CommunicationPreferenceInfo(
    Guid Id,
    MessageCategory Category,
    bool OptedOut,
    bool InboxEnabled,
    Instant UpdatedAt,
    string UpdateSource,
    Instant? SubscribedAt);

/// <summary>
/// Compact projection of an <see cref="EventParticipation"/> row.
/// </summary>
public sealed record EventParticipationInfo(
    Guid Id,
    int Year,
    ParticipationStatus Status,
    ParticipationSource Source,
    Instant? DeclaredAt);

/// <summary>
/// Compact projection of an <c>AspNetUserLogins</c> row.
/// </summary>
public sealed record UserExternalLoginInfo(
    string Provider,
    string ProviderKey);

/// <summary>
/// Immutable projection of a <see cref="Profile"/> row carried inside
/// <see cref="UserInfo"/>. <c>ProfilePictureData</c> is intentionally excluded
/// (large blob, served separately); only <c>BirthdayDay</c> / <c>BirthdayMonth</c>
/// are carried (year-of-birth excluded by design).
/// </summary>
public sealed record ProfileInfo(
    Guid Id,
    string BurnerName,
    string FirstName,
    string LastName,
    string? City,
    string? CountryCode,
    double? Latitude,
    double? Longitude,
    string? PlaceId,
    string? Bio,
    string? Pronouns,
    int? BirthdayDay,
    int? BirthdayMonth,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelationship,
    bool HasCustomPicture,
    string? ProfilePictureContentType,
    Instant CreatedAt,
    Instant UpdatedAt,
    string? AdminNotes,
    string? ContributionInterests,
    string? BoardNotes,
    string? Iban,
    ProfileState? State,
    bool IsApproved,
    MembershipTier MembershipTier,
    ConsentCheckStatus? ConsentCheckStatus,
    Instant? ConsentCheckAt,
    Guid? ConsentCheckedByUserId,
    string? ConsentCheckNotes,
    string? RejectionReason,
    Instant? RejectedAt,
    Guid? RejectedByUserId,
    bool NoPriorBurnExperience,
    IReadOnlyList<ContactFieldInfo> ContactFields,
    IReadOnlyList<ProfileLanguageInfo> Languages,
    IReadOnlyList<VolunteerHistoryInfo> VolunteerHistory)
{
    /// <summary>Full name (FirstName + " " + LastName, trimmed).</summary>
    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>
    /// Best available name for email greetings: BurnerName > FirstName > "there".
    /// </summary>
    public string EmailGreetingName =>
        !string.IsNullOrWhiteSpace(BurnerName) ? BurnerName :
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName : "there";

}


/// <summary>
/// Unified, immutable read-model spanning the User and Profile sections.
/// Issue #703: the canonical "everything-about-a-person" cached entity. Top-level
/// fields mirror the public read surface of <see cref="User"/>; nested
/// <see cref="Profile"/> mirrors <see cref="Domain.Entities.Profile"/>.
/// Designed as a drop-in for consumer code that previously read
/// <c>User</c> and <c>User.Profile</c> directly — symbol substitution is the
/// migration path.
/// </summary>
/// <remarks>
/// <para>
/// Built by <see cref="Create"/> from the 8 contributing tables: <c>users</c>,
/// <c>user_emails</c>, <c>event_participations</c>, <c>user_logins</c> (AspNet),
/// <c>profiles</c>, <c>contact_fields</c>, <c>profile_languages</c>,
/// <c>volunteer_history_entries</c>. Held in a Singleton
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// by the caching decorator. Cache size at 3k users × ~5–15 KB ≈ 15–45 MB —
/// trivial at this scale.
/// </para>
/// <para>
/// Sensitive fields (<see cref="ProfileInfo.Iban"/>,
/// <see cref="ProfileInfo.BoardNotes"/>, <see cref="ProfileInfo.AdminNotes"/>,
/// <see cref="ProfileInfo.EmergencyContactName"/> /
/// <see cref="ProfileInfo.EmergencyContactPhone"/> /
/// <see cref="ProfileInfo.EmergencyContactRelationship"/>) ride along on the
/// cached god-object; visibility filtering is a view-layer concern.
/// </para>
/// <para>
/// <see cref="FullProfile"/> continues to coexist for the Profile-cache read
/// path until follow-up migrations retire it (see issue #703 out-of-scope items).
/// </para>
/// </remarks>
public sealed record UserInfo(
    Guid Id,
    string DisplayName,
    string PreferredLanguage,
    string? ProfilePictureUrl,
    Instant CreatedAt,
    Instant? LastLoginAt,
    Instant? LastConsentReminderSentAt,
    Instant? DeletionRequestedAt,
    Instant? DeletionScheduledFor,
    Instant? DeletionEligibleAfter,
    bool UnsubscribedFromCampaigns,
    Guid? ICalToken,
    bool SuppressScheduleChangeEmails,
    Instant? MagicLinkSentAt,
    GoogleEmailStatus GoogleEmailStatus,
    ContactSource? ContactSource,
    string? ExternalSourceId,
    Guid? MergedToUserId,
    Instant? MergedAt,
    string? IdentityEmailColumn,
    IReadOnlyList<UserEmailInfo> UserEmails,
    IReadOnlyList<EventParticipationInfo> EventParticipations,
    IReadOnlyList<UserExternalLoginInfo> ExternalLogins,
    ProfileInfo? Profile,
    IReadOnlyList<CommunicationPreferenceInfo> CommunicationPreferences)
{
    /// <summary>
    /// Canonical public-facing name. <see cref="ProfileInfo.BurnerName"/>
    /// when a profile exists and has a non-blank burner name; otherwise
    /// <see cref="DisplayName"/> (the legacy Identity column mirror).
    /// <para>
    /// External render callers (avatars, lists, popovers, notifications,
    /// audit-log labels) MUST use this property, not <see cref="DisplayName"/>.
    /// Reading <see cref="DisplayName"/> directly leaks the legacy column
    /// onto public surfaces for any user who has chosen a burner name. The
    /// only legitimate consumers of the raw <see cref="DisplayName"/> field
    /// are debug screens (e.g. <c>/Users/Admin/Debug</c>) and the
    /// <see cref="BurnerName"/> fallback itself.
    /// </para>
    /// </summary>
    public string BurnerName =>
        Profile is not null && !string.IsNullOrWhiteSpace(Profile.BurnerName)
            ? Profile.BurnerName
            : DisplayName;

    /// <summary>
    /// Canonical effective email — first verified UserEmail (primary-preferred),
    /// falling back to the underlying Identity column when no UserEmails are
    /// loaded. Mirrors <see cref="User.Email"/>.
    /// </summary>
    public string? Email
    {
        get
        {
            if (UserEmails.Count == 0)
                return IdentityEmailColumn;

            return UserEmails
                .Where(e => e.IsVerified)
                .OrderByDescending(e => e.IsPrimary)
                .Select(e => e.Email)
                .FirstOrDefault() ?? IdentityEmailColumn;
        }
    }

    /// <summary>
    /// True when any <see cref="UserEmails"/> row is verified — mirrors
    /// <see cref="User.EmailConfirmed"/>.
    /// </summary>
    public bool EmailConfirmed => UserEmails.Any(e => e.IsVerified);

    /// <summary>
    /// Whether a deletion request is pending. Mirrors
    /// <see cref="User.IsDeletionPending"/>.
    /// </summary>
    public bool IsDeletionPending => DeletionRequestedAt.HasValue;

    /// <summary>
    /// First verified <see cref="UserEmailInfo"/> where
    /// <see cref="UserEmailInfo.IsPrimary"/> is true; null when no primary
    /// verified address is loaded.
    /// </summary>
    public string? PrimaryEmail => UserEmails
        .Where(e => e.IsPrimary && e.IsVerified)
        .Select(e => e.Email)
        .FirstOrDefault();

    /// <summary>
    /// First verified <see cref="UserEmailInfo"/> tagged
    /// <see cref="UserEmailInfo.IsGoogle"/>.
    /// </summary>
    public string? GoogleEmail => UserEmails
        .Where(e => e.IsGoogle && e.IsVerified)
        .Select(e => e.Email)
        .FirstOrDefault();

    /// <summary>
    /// Every verified address on the user, primary first.
    /// </summary>
    public IReadOnlyList<string> AllVerifiedEmails => UserEmails
        .Where(e => e.IsVerified)
        .OrderByDescending(e => e.IsPrimary)
        .Select(e => e.Email)
        .ToList();

    /// <summary>
    /// Marketing-category opt-in tri-state: null when no preference row exists
    /// (e.g., user imported from an external source who never hit the prefs
    /// flow), true when opted out, false when opted in.
    /// </summary>
    public bool? MarketingOptedOut => CommunicationPreferences
        .Where(c => c.Category == MessageCategory.Marketing)
        .Select(c => (bool?)c.OptedOut)
        .FirstOrDefault();

    /// <summary>
    /// True when the user has <em>any</em> event participation in the
    /// <see cref="ParticipationStatus.Ticketed"/> or
    /// <see cref="ParticipationStatus.Attended"/> state, across every year.
    /// Year-agnostic — for diagnostic surfaces where "this user is on a
    /// ticket somewhere in the cache" is the useful signal. For year-scoped
    /// counts (dashboard tiles, the Tickets Venn) use
    /// <see cref="HasTicketForYear"/> against the active event year so
    /// counts don't carry stale post-rollover data.
    /// </summary>
    public bool HasTicket => EventParticipations.Any(p =>
        p.Status == ParticipationStatus.Ticketed ||
        p.Status == ParticipationStatus.Attended);

    /// <summary>
    /// True when the user has a <see cref="ParticipationStatus.Ticketed"/>
    /// or <see cref="ParticipationStatus.Attended"/> participation in the
    /// given <paramref name="year"/>. The right predicate for "current ticket
    /// holder" stats — pass the active event year so post-rollover users
    /// stop counting against the new year's totals.
    /// </summary>
    public bool HasTicketForYear(int year) => EventParticipations.Any(p =>
        p.Year == year &&
        (p.Status == ParticipationStatus.Ticketed ||
         p.Status == ParticipationStatus.Attended));

    /// <summary>
    /// True when this user should be treated as a Stub profile — no profile row,
    /// explicit <see cref="ProfileState.Stub"/>, or a legacy <c>null</c> State
    /// row that has not yet been backfilled by
    /// <c>CachingProfileService.PopulateStateIfNullAsync</c>. Paranoid /
    /// defense-in-depth predicate: callers writing consent records or
    /// admitting the user to flows that require a verified legal name MUST
    /// block on this.
    /// </summary>
    public bool IsStub =>
        Profile is null || Profile.State is null or ProfileState.Stub;

    /// <summary>
    /// A regular user of the site: has a profile and hasn't been rejected
    /// (failed consent check). Does NOT require <see cref="ProfileInfo.IsApproved"/>
    /// — Consent Coordinator approval is a separate gate on top of this.
    /// </summary>
    public bool IsActive =>
        Profile is not null && Profile.RejectedAt is null;

    /// <summary>
    /// True when the user has a profile and BurnerName + FirstName + LastName
    /// are all non-blank. The "has a name" predicate — Consent Coordinator
    /// review cannot proceed without it. Reads field values directly, ignores
    /// <see cref="ProfileInfo.State"/> so legacy null-State rows are evaluated
    /// the same way Active rows are.
    /// </summary>
    public bool HasRequiredIdentityFields =>
        Profile is not null
        && !string.IsNullOrWhiteSpace(Profile.BurnerName)
        && !string.IsNullOrWhiteSpace(Profile.FirstName)
        && !string.IsNullOrWhiteSpace(Profile.LastName);

    /// <summary>
    /// True when this user belongs in the Consent Coordinator's review queue —
    /// active (has profile, not rejected), has a legal name, not yet approved.
    /// CC review cannot happen until the user fills in their name, so
    /// <see cref="HasRequiredIdentityFields"/> gates queue inclusion. Single
    /// predicate shared by review-queue and nav-badge call sites so the queue
    /// list and its count cannot drift.
    /// </summary>
    public bool NeedsConsentReview =>
        IsActive && HasRequiredIdentityFields && !Profile!.IsApproved;

    /// <summary>
    /// Builds a <see cref="UserInfo"/> from the 8 contributing tables. Each
    /// input is the raw read-only entity (or empty list) — all snapshotting,
    /// projection, and ordering happens here so the cached payload is immutable.
    /// </summary>
    public static UserInfo Create(
        User user,
        IReadOnlyList<UserEmail> userEmails,
        IReadOnlyList<EventParticipation> eventParticipations,
        IReadOnlyList<(string Provider, string ProviderKey)> externalLogins,
        Profile? profile,
        IReadOnlyList<ContactField> contactFields,
        IReadOnlyList<ProfileLanguage> profileLanguages,
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory,
        IReadOnlyList<CommunicationPreference> communicationPreferences)
    {
        var userEmailInfos = userEmails
            .OrderByDescending(e => e.IsPrimary)
            .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => new UserEmailInfo(
                e.Id, e.Email, e.IsVerified, e.IsPrimary, e.IsGoogle,
                e.Provider, e.ProviderKey, e.Visibility))
            .ToList();

        var participationInfos = eventParticipations
            .OrderBy(p => p.Year)
            .Select(p => new EventParticipationInfo(
                p.Id, p.Year, p.Status, p.Source, p.DeclaredAt))
            .ToList();

        var loginInfos = externalLogins
            .Select(l => new UserExternalLoginInfo(l.Provider, l.ProviderKey))
            .ToList();

        ProfileInfo? profileInfo = null;
        if (profile is not null)
        {
            var contactFieldInfos = contactFields
                .OrderBy(c => c.DisplayOrder)
                .Select(c => new ContactFieldInfo(
                    c.Id, c.FieldType, c.CustomLabel, c.Value, c.Visibility, c.DisplayOrder))
                .ToList();

            var languageInfos = profileLanguages
                .OrderByDescending(l => l.Proficiency)
                .ThenBy(l => l.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .Select(l => new ProfileLanguageInfo(l.Id, l.LanguageCode, l.Proficiency))
                .ToList();

            var volunteerHistoryInfos = volunteerHistory
                .OrderByDescending(v => v.Date)
                .Select(v => new VolunteerHistoryInfo(v.Id, v.Date, v.EventName, v.Description))
                .ToList();

            profileInfo = new ProfileInfo(
                Id: profile.Id,
                BurnerName: profile.BurnerName,
                FirstName: profile.FirstName,
                LastName: profile.LastName,
                City: profile.City,
                CountryCode: profile.CountryCode,
                Latitude: profile.Latitude,
                Longitude: profile.Longitude,
                PlaceId: profile.PlaceId,
                Bio: profile.Bio,
                Pronouns: profile.Pronouns,
                BirthdayDay: profile.DateOfBirth?.Day,
                BirthdayMonth: profile.DateOfBirth?.Month,
                EmergencyContactName: profile.EmergencyContactName,
                EmergencyContactPhone: profile.EmergencyContactPhone,
                EmergencyContactRelationship: profile.EmergencyContactRelationship,
                HasCustomPicture: profile.ProfilePictureData is not null && profile.ProfilePictureData.Length > 0,
                ProfilePictureContentType: profile.ProfilePictureContentType,
                CreatedAt: profile.CreatedAt,
                UpdatedAt: profile.UpdatedAt,
                AdminNotes: profile.AdminNotes,
                ContributionInterests: profile.ContributionInterests,
                BoardNotes: profile.BoardNotes,
                Iban: profile.Iban,
                State: profile.State,
                IsApproved: profile.IsApproved,
                MembershipTier: profile.MembershipTier,
                ConsentCheckStatus: profile.ConsentCheckStatus,
                ConsentCheckAt: profile.ConsentCheckAt,
                ConsentCheckedByUserId: profile.ConsentCheckedByUserId,
                ConsentCheckNotes: profile.ConsentCheckNotes,
                RejectionReason: profile.RejectionReason,
                RejectedAt: profile.RejectedAt,
                RejectedByUserId: profile.RejectedByUserId,
                NoPriorBurnExperience: profile.NoPriorBurnExperience,
                ContactFields: contactFieldInfos,
                Languages: languageInfos,
                VolunteerHistory: volunteerHistoryInfos);
        }

        var communicationPreferenceInfos = communicationPreferences
            .OrderBy(c => c.Category)
            .Select(c => new CommunicationPreferenceInfo(
                c.Id, c.Category, c.OptedOut, c.InboxEnabled,
                c.UpdatedAt, c.UpdateSource, c.SubscribedAt))
            .ToList();

        return new UserInfo(
            Id: user.Id,
            DisplayName: user.DisplayName,
            PreferredLanguage: user.PreferredLanguage,
            ProfilePictureUrl: user.ProfilePictureUrl,
            CreatedAt: user.CreatedAt,
            LastLoginAt: user.LastLoginAt,
            LastConsentReminderSentAt: user.LastConsentReminderSentAt,
            DeletionRequestedAt: user.DeletionRequestedAt,
            DeletionScheduledFor: user.DeletionScheduledFor,
            DeletionEligibleAfter: user.DeletionEligibleAfter,
            UnsubscribedFromCampaigns: user.UnsubscribedFromCampaigns,
            ICalToken: user.ICalToken,
            SuppressScheduleChangeEmails: user.SuppressScheduleChangeEmails,
            MagicLinkSentAt: user.MagicLinkSentAt,
            GoogleEmailStatus: user.GoogleEmailStatus,
            ContactSource: user.ContactSource,
            ExternalSourceId: user.ExternalSourceId,
            MergedToUserId: user.MergedToUserId,
            MergedAt: user.MergedAt,
            IdentityEmailColumn: user.IdentityEmailColumn,
            UserEmails: userEmailInfos,
            EventParticipations: participationInfos,
            ExternalLogins: loginInfos,
            Profile: profileInfo,
            CommunicationPreferences: communicationPreferenceInfos);
    }
}
