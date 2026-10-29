using NodaTime;

namespace Humans.Users.Contracts;

public sealed record UserEmailInfo(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsPrimary,
    [property: Obsolete("Moved to GoogleIntegration's google_workspace_identities (Users decomposition commit 15). Read IGoogleIntegrationService.GetWorkspaceIdentityAsync / GetWorkspaceIdentitiesAsync; the one reader left is GoogleIntegration's legacy fallback and the google.workspace-identities copy.", DiagnosticId = "HUM_USER_GOOGLE_IDENTITY")]
    bool IsGoogle,
    string? Provider,
    string? ProviderKey,
    ContactFieldVisibility? Visibility,
    Instant? VerificationSentAt,
    Instant CreatedAt,
    Instant UpdatedAt,
    [property: Obsolete("Moved to GoogleIntegration's google_workspace_identities (Users decomposition commit 15). Read IGoogleIntegrationService.GetWorkspaceIdentityAsync / GetWorkspaceIdentitiesAsync; the one reader left is GoogleIntegration's legacy fallback and the google.workspace-identities copy.", DiagnosticId = "HUM_USER_GOOGLE_IDENTITY")]
    LegacyGoogleEmailStatus GoogleEmailStatus);

public sealed record ContactFieldInfo(
    Guid Id,
    ContactFieldType FieldType,
    string? CustomLabel,
    string Value,
    ContactFieldVisibility Visibility,
    int DisplayOrder);

public sealed record ProfileLanguageInfo(
    Guid Id,
    string LanguageCode,
    LanguageProficiency Proficiency);

public sealed record VolunteerHistoryInfo(
    Guid Id,
    LocalDate Date,
    string EventName,
    string? Description);

/// <summary>The frozen legacy <c>communication_preferences</c> row; the live one is Email's.</summary>
public sealed record LegacyCommunicationPreferenceInfo(
    Guid Id,
    LegacyMessageCategory Category,
    bool OptedOut,
    bool InboxEnabled,
    Instant UpdatedAt,
    string UpdateSource,
    Instant? SubscribedAt);

/// <summary>The frozen legacy row; the live one is Tickets' <c>EventParticipationInfo</c>.</summary>
public sealed record LegacyEventParticipationInfo(
    Guid Id,
    int Year,
    LegacyParticipationStatus Status,
    LegacyParticipationSource Source,
    Instant? DeclaredAt,
    Instant? CheckedInAt);

public sealed record UserExternalLoginInfo(
    string Provider,
    string ProviderKey);

/// <summary>No picture bytes, and the birthday carries day and month only (no year).</summary>
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
    [property: Obsolete("Moved to Cantina's cantina_dietary_profiles (Users decomposition commit 10). Read ICantinaService; the one reader left is Cantina's legacy fallback and the cantina.profile-dietary copy.", DiagnosticId = "HUM_PROFILE_DIETARY")]
    string? DietaryPreference,
    [property: Obsolete("Moved to Cantina's cantina_dietary_profiles (Users decomposition commit 10). Read ICantinaService; the one reader left is Cantina's legacy fallback and the cantina.profile-dietary copy.", DiagnosticId = "HUM_PROFILE_DIETARY")]
    IReadOnlyList<string> Allergies,
    [property: Obsolete("Moved to Cantina's cantina_dietary_profiles (Users decomposition commit 10). Read ICantinaService; the one reader left is Cantina's legacy fallback and the cantina.profile-dietary copy.", DiagnosticId = "HUM_PROFILE_DIETARY")]
    string? AllergyOtherText,
    [property: Obsolete("Moved to Cantina's cantina_dietary_profiles (Users decomposition commit 10). Read ICantinaService; the one reader left is Cantina's legacy fallback and the cantina.profile-dietary copy.", DiagnosticId = "HUM_PROFILE_DIETARY")]
    IReadOnlyList<string> Intolerances,
    [property: Obsolete("Moved to Cantina's cantina_dietary_profiles (Users decomposition commit 10). Read ICantinaService; the one reader left is Cantina's legacy fallback and the cantina.profile-dietary copy.", DiagnosticId = "HUM_PROFILE_DIETARY")]
    string? IntoleranceOtherText,
    // GDPR Art. 9: every render or serialize surface must gate this behind the MedicalDataViewer policy.
    string? MedicalConditions,
    bool HasCustomPicture,
    string? ProfilePictureContentType,
    Instant CreatedAt,
    Instant UpdatedAt,
    string? AdminNotes,
    string? ContributionInterests,
    string? BoardNotes,
    [property: Obsolete("Moved to Finance's finance_payment_details (Users decomposition commit 1). Read IFinanceService.GetIbanAsync; the one reader left is Finance's legacy fallback and the finance.iban-to-payment-details copy.", DiagnosticId = "HUM_PROFILE_IBAN")]
    string? Iban,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    bool IsApproved,
    [property: Obsolete("Moved to Governance's governance_membership_tiers (Users decomposition commit 11). Read IGovernanceService.GetMembershipTierAsync / GetMembershipTiersAsync; the one reader left is Governance's legacy fallback and the governance.profile-tier copy.", DiagnosticId = "HUM_PROFILE_TIER")]
    LegacyMembershipTier MembershipTier,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    LegacyConsentCheckStatus? ConsentCheckStatus,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    Instant? ConsentCheckAt,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    Guid? ConsentCheckedByUserId,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    string? ConsentCheckNotes,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    string? RejectionReason,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    Instant? RejectedAt,
    [property: Obsolete("Moved to Consent's consent_reviews (Users decomposition commit 12). Read IConsentService.GetConsentReviewAsync / GetConsentReviewsAsync, or UserInfo.IsRejected for the rejection state; the readers left are Consent's legacy fallback, the consent.profile-review copy and UserInfo.IsRejected's legacy arm.", DiagnosticId = "HUM_PROFILE_REVIEW")]
    Guid? RejectedByUserId,
    bool NoPriorBurnExperience,
    IReadOnlyList<ContactFieldInfo> ContactFields,
    IReadOnlyList<ProfileLanguageInfo> Languages,
    IReadOnlyList<VolunteerHistoryInfo> VolunteerHistory)
{
    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>BurnerName, else FirstName, else "there".</summary>
    public string EmailGreetingName =>
        !string.IsNullOrWhiteSpace(BurnerName) ? BurnerName :
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName : "there";

}


/// <summary>Everything about a person, cached. Sensitive fields ride along: visibility filtering is the view's job.</summary>
public sealed record UserInfo(
    Guid Id,
    string BurnerName,
    bool IsGdprAnonymized,
    string PreferredLanguage,
    string? FallbackPictureUrl,
    Instant CreatedAt,
    Instant? LastLoginAt,
    Instant? DeletionRequestedAt,
    Instant? DeletionScheduledFor,
    Instant? DeletionEligibleAfter,
    ContactSource? ContactSource,
    string? ExternalSourceId,
    Guid? MergedToUserId,
    Instant? MergedAt,
    string? IdentityEmailColumn,
    IReadOnlyList<UserEmailInfo> UserEmails,
    [property: Obsolete("Moved to Tickets' tickets_event_participations (Users decomposition commit 13). Read ITicketService.GetParticipationsAsync / GetAllParticipationsAsync; the one reader left is Tickets' legacy fallback and the tickets.event-participations copy.", DiagnosticId = "HUM_USER_PARTICIPATIONS")]
    IReadOnlyList<LegacyEventParticipationInfo> EventParticipations,
    IReadOnlyList<UserExternalLoginInfo> ExternalLogins,
    ProfileInfo? Profile,
    [property: Obsolete("Moved to Email's email_communication_preferences (Users decomposition commit 14). Read ICommunicationPreferenceService; the one reader left is Email's legacy fallback and the email.communication-preferences copy.", DiagnosticId = "HUM_USER_COMMPREFS")]
    IReadOnlyList<LegacyCommunicationPreferenceInfo> CommunicationPreferences)
{
    /// <summary>The single source of truth for access; only <see cref="UserState.Active"/> has full access.</summary>
    public UserState State { get; init; }

    /// <summary>
    /// Every id whose merge chain passes through this row, transitively, sorted: in A→B→C, C carries <c>[A, B]</c>.
    /// Callers union by these ids; nobody walks a chain. Stamped by the caching decorator, the only thing that sees
    /// the whole graph, so a record built by <see cref="Create"/> always has <c>[]</c>.
    /// </summary>
    public IReadOnlyList<Guid> MergedUserIds { get; init; } = [];

    /// <summary>
    /// <see cref="Id"/> first, then <see cref="MergedUserIds"/>: what a per-user read of an append-only table queries
    /// by. Seeded from this record's id, never the id asked with, which may be an archived one.
    /// </summary>
    public IReadOnlyList<Guid> AllUserIds => MergedUserIds.Count == 0 ? [Id] : [Id, .. MergedUserIds];

    /// <summary>
    /// The only source of picture URLs: the custom upload when present, else the legacy
    /// <see cref="User.ProfilePictureUrl"/>.
    /// </summary>
    public string? ProfilePictureUrl =>
        Profile is { HasCustomPicture: true }
            ? $"/Profile/Picture?id={Profile.Id}&v={Profile.UpdatedAt.ToUnixTimeTicks()}"
            : FallbackPictureUrl;

    /// <summary>Mirrors <see cref="User.Email"/>.</summary>
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

    public bool EmailConfirmed => UserEmails.Any(e => e.IsVerified);

    public bool IsDeletionPending => DeletionRequestedAt.HasValue;

    /// <summary>The grace deadline, extended by any later ticket hold.</summary>
    public Instant? EffectiveDeletionDate =>
        DeletionScheduledFor is { } scheduled && DeletionEligibleAfter is { } hold && hold > scheduled
            ? hold : DeletionScheduledFor;

    public bool IsMerged => MergedAt is not null;

    public const string GdprAnonymizedBurnerName = UserStateClassifier.GdprAnonymizedDisplayName;

    /// <summary>
    /// A merge source, a GDPR-erased row, or a legacy tombstone whose address still ends in <c>.local</c> (older
    /// merges and purges). Callers that create per-user rows (Stub Profile and the like) must short-circuit on it so
    /// they don't resurrect the tombstone. Must cover every row <c>UserStateEvaluator.IsTombstone</c> does, or a
    /// caller passes this check and the write is refused as an unavailable account.
    /// </summary>
    public bool IsTombstone =>
        MergedAt is not null
        || MergedToUserId is not null
        || State is UserState.Deleted or UserState.Merged
        || IsGdprAnonymized
        || IsLocalSentinel(IdentityEmailColumn)
        || IsLocalSentinel(Email);

    private static bool IsLocalSentinel(string? email) =>
        email is not null && email.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    public string? PrimaryEmail => UserEmails
        .Where(e => e.IsPrimary && e.IsVerified)
        .Select(e => e.Email)
        .FirstOrDefault();

    /// <summary>Primary first.</summary>
    public IReadOnlyList<string> AllVerifiedEmails => UserEmails
        .Where(e => e.IsVerified)
        .OrderByDescending(e => e.IsPrimary)
        .Select(e => e.Email)
        .ToList();

    /// <summary>No profile or blank required names. Callers writing consents must block on it.</summary>
    public bool IsStub => State == UserState.Bare;

    /// <summary>
    /// Excludes tombstones explicitly: a tombstone keeps its anonymized profile, so "has a profile" isn't enough.
    /// Doesn't consult the Consent Coordinator review, which is an annotation, not a gate.
    /// </summary>
    public bool IsActive =>
        Profile is not null && State != UserState.Rejected && !IsTombstone;

    /// <summary>The canonical suspended predicate (memory/code/no-issuspended.md).</summary>
    public bool IsSuspended =>
        State is UserState.Suspended or UserState.AdminSuspended;

    /// <summary>
    /// Stored <see cref="UserState.Rejected"/>, or (until the <c>consent.profile-review</c> copy is done) a legacy
    /// profile rejection time. A tombstone reads its final state instead; Consent keeps the rejection record.
    /// </summary>
    public bool IsRejected =>
        State == UserState.Rejected
#pragma warning disable HUM_PROFILE_REVIEW // legacy arm, retired with the consent.profile-review copy
        || Profile?.RejectedAt is not null;
#pragma warning restore HUM_PROFILE_REVIEW

    /// <summary>The population person search and the single-person lookups resolve.</summary>
    public bool IsSearchable => Profile is not null && !IsRejected;

    public bool HasProfile => Profile is not null;

    /// <summary>
    /// Non-blank BurnerName, FirstName and LastName. Gates Stub→Active, the review queue, consents, transfers and
    /// signup. Ignores State, so legacy null-State rows behave like Active ones.
    /// </summary>
    public bool HasRequiredNameFields =>
        Profile is not null
        && !string.IsNullOrWhiteSpace(Profile.BurnerName)
        && !string.IsNullOrWhiteSpace(Profile.FirstName)
        && !string.IsNullOrWhiteSpace(Profile.LastName);

    /// <summary>
    /// Snapshots and orders here, so the cached payload is immutable. Takes projections because the Profile-side
    /// entities are internal to <c>Humans.Users</c>; public because the section test projects build UserInfo with it.
    /// </summary>
    public static UserInfo Create(
        User user,
        IReadOnlyList<UserEmail> userEmails,
        IReadOnlyList<EventParticipation> eventParticipations,
        IReadOnlyList<(string Provider, string ProviderKey)> externalLogins,
        ProfileInfo? profile,
        IReadOnlyList<LegacyCommunicationPreferenceInfo> communicationPreferences)
    {
        var userEmailInfos = userEmails
            .OrderByDescending(e => e.IsPrimary)
            .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => new UserEmailInfo(
                e.Id, e.Email, e.IsVerified, e.IsPrimary, e.IsGoogle,
                e.Provider, e.ProviderKey, e.Visibility, e.VerificationSentAt,
                e.CreatedAt, e.UpdatedAt, e.GoogleEmailStatus))
            .ToList();

        var participationInfos = eventParticipations
            .OrderBy(p => p.Year)
            .Select(p => new LegacyEventParticipationInfo(
                p.Id, p.Year, p.Status, p.Source, p.DeclaredAt, p.CheckedInAt))
            .ToList();

        var loginInfos = externalLogins
            .Select(l => new UserExternalLoginInfo(l.Provider, l.ProviderKey))
            .ToList();

        var communicationPreferenceInfos = communicationPreferences
            .OrderBy(c => c.Category)
            .ToList();

        var isGdprAnonymized = IsGdprTombstone(user);
        // Some erased rows carry the minted email with a blank BurnerName.
        var burnerName = !string.IsNullOrWhiteSpace(user.BurnerName) ? user.BurnerName
            : isGdprAnonymized ? GdprAnonymizedBurnerName
            : string.Empty;

        var info = new UserInfo(
            Id: user.Id,
            BurnerName: burnerName,
            IsGdprAnonymized: isGdprAnonymized,
            PreferredLanguage: user.PreferredLanguage,
            FallbackPictureUrl: user.ProfilePictureUrl,
            CreatedAt: user.CreatedAt,
            LastLoginAt: user.LastLoginAt,
            DeletionRequestedAt: user.DeletionRequestedAt,
            DeletionScheduledFor: user.DeletionScheduledFor,
            DeletionEligibleAfter: user.DeletionEligibleAfter,
            ContactSource: user.ContactSource,
            ExternalSourceId: user.ExternalSourceId,
            MergedToUserId: user.MergedToUserId,
            MergedAt: user.MergedAt,
            IdentityEmailColumn: user.IdentityEmailColumn,
            UserEmails: userEmailInfos,
            EventParticipations: participationInfos,
            ExternalLogins: loginInfos,
            Profile: profile,
            CommunicationPreferences: communicationPreferenceInfos)
        {
            State = user.State,
        };

        return info;
    }

    /// <summary>
    /// Keyed on the <c>deleted-&lt;id&gt;@deleted.local</c> address erasure mints, never on a name a member can type.
    /// Reads <see cref="User.IdentityEmailColumn"/>: erasure removes the <c>UserEmail</c> rows first. Twin of
    /// <c>UserStateEvaluator.IsGdprTombstoned</c>, which is internal to the section: change both together.
    /// </summary>
    private static bool IsGdprTombstone(User user) =>
        user.IdentityEmailColumn is { } email
        && email.EndsWith("@deleted.local", StringComparison.OrdinalIgnoreCase);
}
