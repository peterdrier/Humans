using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application;

/// <summary>Compact projection of <see cref="UserEmail"/> carried inside <see cref="UserInfo"/>.</summary>
public sealed record UserEmailInfo(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsPrimary,
    bool IsGoogle,
    string? Provider,
    string? ProviderKey,
    ContactFieldVisibility? Visibility,
    Instant? VerificationSentAt,
    Instant CreatedAt,
    Instant UpdatedAt);

/// <summary>Compact projection of <see cref="ContactField"/> carried inside <see cref="ProfileInfo"/>.</summary>
public sealed record ContactFieldInfo(
    Guid Id,
    ContactFieldType FieldType,
    string? CustomLabel,
    string Value,
    ContactFieldVisibility Visibility,
    int DisplayOrder);

/// <summary>Compact projection of <see cref="ProfileLanguage"/>.</summary>
public sealed record ProfileLanguageInfo(
    Guid Id,
    string LanguageCode,
    LanguageProficiency Proficiency);

/// <summary>Compact projection of <see cref="VolunteerHistoryEntry"/>.</summary>
public sealed record VolunteerHistoryInfo(
    Guid Id,
    LocalDate Date,
    string EventName,
    string? Description);

/// <summary>Compact projection of <see cref="CommunicationPreference"/>.</summary>
public sealed record CommunicationPreferenceInfo(
    Guid Id,
    MessageCategory Category,
    bool OptedOut,
    bool InboxEnabled,
    Instant UpdatedAt,
    string UpdateSource,
    Instant? SubscribedAt);

/// <summary>Compact projection of <see cref="EventParticipation"/>.</summary>
public sealed record EventParticipationInfo(
    Guid Id,
    int Year,
    ParticipationStatus Status,
    ParticipationSource Source,
    Instant? DeclaredAt,
    Instant? CheckedInAt);

/// <summary>Compact projection of an <c>AspNetUserLogins</c> row.</summary>
public sealed record UserExternalLoginInfo(
    string Provider,
    string ProviderKey);

/// <summary>
/// Immutable projection of <see cref="Profile"/> carried inside <see cref="UserInfo"/>. Picture bytes excluded
/// (served via ProfileController.Picture); only birthday day+month carried (no year).
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

    /// <summary>Email greeting name: BurnerName > FirstName > "there".</summary>
    public string EmailGreetingName =>
        !string.IsNullOrWhiteSpace(BurnerName) ? BurnerName :
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName : "there";

}


/// <summary>
/// Canonical "everything-about-a-person" cached read-model spanning User + Profile sections — see #703.
/// Built by <see cref="Create"/> from 8 contributing tables. Sensitive fields ride along; visibility filtering is view-layer.
/// </summary>
public sealed record UserInfo(
    Guid Id,
    [property: Obsolete("Rendering callers must use UserInfo.BurnerName / <vc:human> — DisplayName is the raw legacy column mirror. See memory/architecture/burnername-is-the-display-name.md.", DiagnosticId = "HUM_USERINFO_DISPLAYNAME", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/691")] string DisplayName,
    string PreferredLanguage,
    string? FallbackPictureUrl,
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
    /// Canonical public-facing name: <see cref="ProfileInfo.BurnerName"/> when set, else <see cref="DisplayName"/>.
    /// External render callers MUST use this — reading <see cref="DisplayName"/> directly leaks the legacy column.
    /// </summary>
    public string BurnerName =>
        Profile is not null && !string.IsNullOrWhiteSpace(Profile.BurnerName)
            ? Profile.BurnerName
            : DisplayName;

    /// <summary>
    /// Canonical profile picture URL. Custom upload served from the file share via
    /// <c>/Profile/Picture?id={ProfileId}&amp;v={ticks}</c> when present, otherwise the
    /// legacy <see cref="User.ProfilePictureUrl"/> column as a fallback. This is the ONLY
    /// place profile picture URLs come from across the application.
    /// </summary>
    public string? ProfilePictureUrl =>
        Profile is { HasCustomPicture: true }
            ? $"/Profile/Picture?id={Profile.Id}&v={Profile.UpdatedAt.ToUnixTimeTicks()}"
            : FallbackPictureUrl;

    /// <summary>Effective email — first verified UserEmail (primary-preferred), falling back to Identity column. Mirrors <see cref="User.Email"/>.</summary>
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

    /// <summary>Any UserEmails row verified — mirrors <see cref="User.EmailConfirmed"/>.</summary>
    public bool EmailConfirmed => UserEmails.Any(e => e.IsVerified);

    /// <summary>Deletion request pending — mirrors <see cref="User.IsDeletionPending"/>.</summary>
    public bool IsDeletionPending => DeletionRequestedAt.HasValue;

    /// <summary>Account was merged into another account — the row is a merge-source tombstone.</summary>
    public bool IsMerged => MergedAt is not null;

    /// <summary>
    /// Sentinel <see cref="DisplayName"/> value written by
    /// <see cref="Humans.Application.Interfaces.Repositories.IUserRepository.ApplyExpiredDeletionAnonymizationAsync"/>
    /// to mark GDPR-deleted users. Read by <see cref="IsTombstone"/>; the
    /// shared constant ties write-path and read-path together so a rename
    /// can't silently break tombstone detection.
    /// </summary>
    public const string GdprAnonymizedDisplayName = "Deleted User";

    /// <summary>
    /// True when the user row is a tombstone — a merge-source
    /// (<see cref="MergedAt"/> set), a GDPR-anonymized record (DisplayName
    /// rewritten to <see cref="GdprAnonymizedDisplayName"/> by
    /// <see cref="Humans.Application.Interfaces.Repositories.IUserRepository.ApplyExpiredDeletionAnonymizationAsync"/>),
    /// or a legacy tombstone whose <see cref="Email"/> still ends in the
    /// sentinel <c>.local</c> suffix (pre-<c>MergedAt</c>-column merges and
    /// historic purges wrote <c>@merged.local</c> / <c>@deleted.local</c>
    /// addresses — those rows survive in production with neither
    /// <see cref="MergedAt"/> nor the <see cref="GdprAnonymizedDisplayName"/>
    /// marker set).
    /// Callers that materialize new per-user rows (Stub Profile, etc.) MUST
    /// short-circuit on this so they don't resurrect the tombstone.
    /// </summary>
    public bool IsTombstone =>
        MergedAt is not null
        || string.Equals(DisplayName, GdprAnonymizedDisplayName, StringComparison.Ordinal)
        || (Email is { } email && email.EndsWith(".local", StringComparison.OrdinalIgnoreCase));

    /// <summary>First verified primary email; null when none loaded.</summary>
    public string? PrimaryEmail => UserEmails
        .Where(e => e.IsPrimary && e.IsVerified)
        .Select(e => e.Email)
        .FirstOrDefault();

    /// <summary>First verified IsGoogle UserEmail.</summary>
    public string? GoogleEmail => UserEmails
        .Where(e => e.IsGoogle && e.IsVerified)
        .Select(e => e.Email)
        .FirstOrDefault();

    /// <summary>All verified addresses, primary first.</summary>
    public IReadOnlyList<string> AllVerifiedEmails => UserEmails
        .Where(e => e.IsVerified)
        .OrderByDescending(e => e.IsPrimary)
        .Select(e => e.Email)
        .ToList();

    /// <summary>Marketing opt tri-state: null = no preference row, true = opted out, false = opted in.</summary>
    public bool? MarketingOptedOut => CommunicationPreferences
        .Where(c => c.Category == MessageCategory.Marketing)
        .Select(c => (bool?)c.OptedOut)
        .FirstOrDefault();

    /// <summary>Any-year Ticketed/Attended participation — diagnostic only. Use <see cref="HasTicketForYear"/> for year-scoped counts.</summary>
    public bool HasTicket => EventParticipations.Any(p =>
        p.Status == ParticipationStatus.Ticketed ||
        p.Status == ParticipationStatus.Attended);

    /// <summary>Ticketed/Attended participation for the given <paramref name="year"/> — canonical "current ticket holder" predicate.</summary>
    public bool HasTicketForYear(int year) => EventParticipations.Any(p =>
        p.Year == year &&
        (p.Status == ParticipationStatus.Ticketed ||
         p.Status == ParticipationStatus.Attended));

    /// <summary>
    /// On-site for the given <paramref name="year"/> when an Attended row with
    /// a non-null <see cref="EventParticipationInfo.CheckedInAt"/> exists.
    /// Returns the gate-arrival instant or null. Drives the profile "Onsite
    /// since {time}" chip (issue nobodies-collective/Humans#736).
    /// </summary>
    public Instant? OnsiteSinceForYear(int year) => EventParticipations
        .Where(p => p.Year == year
            && p.Status == ParticipationStatus.Attended
            && p.CheckedInAt is not null)
        .Select(p => p.CheckedInAt)
        .FirstOrDefault();

    /// <summary>Stub profile: no profile row, explicit Stub state, or legacy null State. Callers writing consents must block on this.</summary>
    public bool IsStub =>
        Profile is null || Profile.State is null or ProfileState.Stub;

    /// <summary>Has profile and not rejected. Does NOT require <see cref="ProfileInfo.IsApproved"/> (separate Consent Coordinator gate).</summary>
    public bool IsActive =>
        Profile is not null && Profile.RejectedAt is null;

    /// <summary>Canonical "suspended" predicate — see memory/code/no-issuspended.md.</summary>
    public bool IsSuspended =>
        Profile?.State == ProfileState.Suspended;

    /// <summary>Canonical "approved by Consent Coordinator" predicate — see memory/architecture/derived-predicates-on-userinfo.md.</summary>
    public bool IsApproved => Profile?.IsApproved ?? false;

    /// <summary>Canonical "has profile" predicate.</summary>
    public bool HasProfile => Profile is not null;

    /// <summary>
    /// Has profile with non-blank BurnerName + FirstName + LastName. Gates Stub→Active, CC review queue, consents, transfers, signup.
    /// Ignores State so legacy null-State rows behave like Active rows.
    /// </summary>
    public bool HasRequiredNameFields =>
        Profile is not null
        && !string.IsNullOrWhiteSpace(Profile.BurnerName)
        && !string.IsNullOrWhiteSpace(Profile.FirstName)
        && !string.IsNullOrWhiteSpace(Profile.LastName);

    /// <summary>In CC review queue: active, named, not yet approved. Shared by queue list + nav badge so they cannot drift.</summary>
    public bool NeedsConsentReview =>
        IsActive && HasRequiredNameFields && !Profile!.IsApproved;

    /// <summary>Builds <see cref="UserInfo"/> from the 8 contributing tables; snapshotting + ordering happen here so the cached payload is immutable.</summary>
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
                e.Provider, e.ProviderKey, e.Visibility, e.VerificationSentAt,
                e.CreatedAt, e.UpdatedAt))
            .ToList();

        var participationInfos = eventParticipations
            .OrderBy(p => p.Year)
            .Select(p => new EventParticipationInfo(
                p.Id, p.Year, p.Status, p.Source, p.DeclaredAt, p.CheckedInAt))
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
                HasCustomPicture: profile.ProfilePictureContentType is not null,
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
            FallbackPictureUrl: user.ProfilePictureUrl,
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
