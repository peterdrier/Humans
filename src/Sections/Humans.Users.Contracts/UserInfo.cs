using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>Compact projection of <see cref="UserEmail"/> carried inside <see cref="UserInfo"/>.</summary>
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

/// <summary>Compact projection of <c>ContactField</c> carried inside <see cref="ProfileInfo"/>.</summary>
public sealed record ContactFieldInfo(
    Guid Id,
    ContactFieldType FieldType,
    string? CustomLabel,
    string Value,
    ContactFieldVisibility Visibility,
    int DisplayOrder);

/// <summary>Compact projection of <c>ProfileLanguage</c>.</summary>
public sealed record ProfileLanguageInfo(
    Guid Id,
    string LanguageCode,
    LanguageProficiency Proficiency);

/// <summary>Compact projection of <c>VolunteerHistoryEntry</c>.</summary>
public sealed record VolunteerHistoryInfo(
    Guid Id,
    LocalDate Date,
    string EventName,
    string? Description);

/// <summary>Compact projection of the frozen legacy <c>communication_preferences</c> row
/// (Users decomposition commit 14; the live record is Email's <c>email_communication_preferences</c>).</summary>
public sealed record LegacyCommunicationPreferenceInfo(
    Guid Id,
    LegacyMessageCategory Category,
    bool OptedOut,
    bool InboxEnabled,
    Instant UpdatedAt,
    string UpdateSource,
    Instant? SubscribedAt);

/// <summary>Compact projection of the frozen legacy <see cref="EventParticipation"/> row
/// (Users decomposition commit 13; the live record is Tickets' <c>EventParticipationInfo</c>).</summary>
public sealed record LegacyEventParticipationInfo(
    Guid Id,
    int Year,
    LegacyParticipationStatus Status,
    LegacyParticipationSource Source,
    Instant? DeclaredAt,
    Instant? CheckedInAt);

/// <summary>Compact projection of an <c>AspNetUserLogins</c> row.</summary>
public sealed record UserExternalLoginInfo(
    string Provider,
    string ProviderKey);

/// <summary>
/// Immutable projection of <c>Profile</c> carried inside <see cref="UserInfo"/>. Picture bytes excluded
/// (served via ProfileViewController.Picture); only birthday day+month carried (no year).
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
    // GDPR Art. 9 — present on the cached read-model, but every render/serialize
    // surface MUST gate this behind the MedicalDataViewer policy. Never expose unscoped.
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
    /// <summary>Full name (FirstName + " " + LastName, trimmed).</summary>
    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>Email greeting name: BurnerName > FirstName > "there".</summary>
    public string EmailGreetingName =>
        !string.IsNullOrWhiteSpace(BurnerName) ? BurnerName :
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName : "there";

}


/// <summary>
/// Canonical "everything-about-a-person" cached read-model spanning User + Profile sections — see #703.
/// Built by <see cref="Create"/> from the contributing tables. Sensitive fields ride along; visibility filtering is view-layer.
/// </summary>
public sealed record UserInfo(
    Guid Id,
    string BurnerName,
    bool IsGdprAnonymized,
    string PreferredLanguage,
    string? FallbackPictureUrl,
    Instant CreatedAt,
    Instant? LastLoginAt,
    [property: Obsolete("Moved to Consent's consent_reminders. The one reader left is Consent's ConsentReminderService: its legacy fallback and the consent.reminder-stamps copy.", DiagnosticId = "HUM_USER_CONSENT_REMINDER")]
    Instant? LastConsentReminderSentAt,
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
    /// <summary>
    /// Stored lifecycle/access state (the <c>users.State</c> column) — the single source of truth
    /// for access; <see cref="UserState.Active"/> is the only state with full app access.
    /// </summary>
    public UserState State { get; init; }

    /// <summary>
    /// Every id whose <see cref="MergedToUserId"/> chain passes through this row,
    /// transitively, sorted by id. In an A→B→C merge chain, C carries <c>[A, B]</c>.
    /// Empty for a row nothing was merged into.
    /// <para>
    /// This is the single answer to "which archived accounts are this human" — the
    /// rows AuditLog, Consent, Budget, Expenses and Governance's assembly-vote rosters
    /// deliberately keep keyed to the archived id. Callers union by these ids; nobody
    /// walks a chain.
    /// </para>
    /// <para>
    /// Stamped by the Users caching decorator, the only thing that sees the whole graph.
    /// A record built straight from one row (<see cref="Create"/>) always has <c>[]</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<Guid> MergedUserIds { get; init; } = [];

    /// <summary>
    /// Every id this human has held: <see cref="Id"/> first, then <see cref="MergedUserIds"/>.
    /// The list a per-user read of an append-only table queries by (audit rows, consent
    /// records, roster rows and ballots stay keyed to the archived id on purpose). Seeded from
    /// this record's own id, never from the id the caller asked with: the cross-section reads
    /// resolve a tombstone forward, so the id asked with may be one of the archived ones and
    /// the survivor's own rows would otherwise be the ones left out.
    /// </summary>
    public IReadOnlyList<Guid> AllUserIds => MergedUserIds.Count == 0 ? [Id] : [Id, .. MergedUserIds];

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

    /// <summary>Earliest deletion eligibility: the grace deadline, extended by any later ticket hold.</summary>
    public Instant? EffectiveDeletionDate =>
        DeletionScheduledFor is { } scheduled && DeletionEligibleAfter is { } hold && hold > scheduled
            ? hold : DeletionScheduledFor;

    /// <summary>Account was merged into another account — the row is a merge-source tombstone.</summary>
    public bool IsMerged => MergedAt is not null;

    /// <summary>
    /// Sentinel value written to the legacy User.DisplayName column by
    /// <c>IUserRepository.ApplyExpiredDeletionAnonymizationAsync</c>
    /// to mark GDPR-deleted users. Read into <see cref="IsGdprAnonymized"/>
    /// at creation time so the legacy name never becomes a public UserInfo field.
    /// </summary>
    public const string GdprAnonymizedBurnerName = UserStateClassifier.GdprAnonymizedDisplayName;

    /// <summary>
    /// True when the user row is a tombstone — a merge-source
    /// (<see cref="MergedAt"/> set), a GDPR-anonymized record (legacy User.DisplayName
    /// resolved into <see cref="BurnerName"/> as <see cref="GdprAnonymizedBurnerName"/> by
    /// <c>IUserRepository.ApplyExpiredDeletionAnonymizationAsync</c>),
    /// or a legacy tombstone whose <see cref="Email"/> still ends in the
    /// sentinel <c>.local</c> suffix (pre-<c>MergedAt</c>-column merges and
    /// historic purges wrote <c>@merged.local</c> / <c>@deleted.local</c>
    /// addresses — those rows survive in production with neither
    /// <see cref="MergedAt"/> nor the <see cref="GdprAnonymizedBurnerName"/>
    /// marker set).
    /// Callers that materialize new per-user rows (Stub Profile, etc.) MUST
    /// short-circuit on this so they don't resurrect the tombstone.
    /// </summary>
    public bool IsTombstone =>
        MergedAt is not null
        || IsGdprAnonymized
        || (Email is { } email && email.EndsWith(".local", StringComparison.OrdinalIgnoreCase));

    /// <summary>First verified primary email; null when none loaded.</summary>
    public string? PrimaryEmail => UserEmails
        .Where(e => e.IsPrimary && e.IsVerified)
        .Select(e => e.Email)
        .FirstOrDefault();

    /// <summary>All verified addresses, primary first.</summary>
    public IReadOnlyList<string> AllVerifiedEmails => UserEmails
        .Where(e => e.IsVerified)
        .OrderByDescending(e => e.IsPrimary)
        .Select(e => e.Email)
        .ToList();

    /// <summary>Stub: hasn't entered their name yet (<see cref="UserState.Bare"/>) — no profile row
    /// or blank required names. Callers writing consents must block on this.</summary>
    public bool IsStub => State == UserState.Bare;

    /// <summary>Has a profile, is not rejected, and is not a merge/deletion tombstone (a tombstone
    /// keeps its anonymized Profile row, so that check alone isn't enough — peterdrier/Humans#1707).
    /// Does NOT consult the Consent Coordinator review (Consent's annotation, not a gate).
    /// Reads rejection off the stored <see cref="State"/>.</summary>
    public bool IsActive =>
        Profile is not null && State != UserState.Rejected && !IsTombstone;

    /// <summary>Canonical "suspended" predicate — see memory/code/no-issuspended.md. Derives from the
    /// stored <see cref="State"/>, which is where suspension itself lives.</summary>
    public bool IsSuspended =>
        State is UserState.Suspended or UserState.AdminSuspended;

    /// <summary>
    /// The signup was rejected: the stored <see cref="State"/> is <see cref="UserState.Rejected"/>,
    /// or (legacy arm, until the <c>consent.profile-review</c> copy is done in production) the
    /// frozen profile still carries a rejection time. Exact for live accounts; a merged or deleted
    /// tombstone reads its final state instead — Consent's review (<c>IConsentService</c>) keeps the
    /// rejection record itself.
    /// </summary>
    public bool IsRejected =>
        State == UserState.Rejected
#pragma warning disable HUM_PROFILE_REVIEW // legacy arm, retired with the consent.profile-review copy
        || Profile?.RejectedAt is not null;
#pragma warning restore HUM_PROFILE_REVIEW

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

    /// <summary>
    /// Builds <see cref="UserInfo"/> from the projections of the contributing tables;
    /// snapshotting + ordering happen here so the cached payload is immutable. The six
    /// Profile-side entities are internal to <c>Humans.Users</c>, so the entity-taking factory is
    /// <c>Humans.Users.Services.UserInfoFactory</c> and this overload names none of them — it
    /// stays public because thirty section test projects build a <see cref="UserInfo"/> through
    /// it and internalising it would trade six <c>InternalsVisibleTo</c> grants for thirty
    /// (nobodies-collective/Humans#1051).
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

        var legacyDisplayName = user.DisplayName;
        var burnerName = ResolveBurnerName(user.BurnerName, legacyDisplayName);
        var isGdprAnonymized = IsGdprTombstone(user);

        var info = new UserInfo(
            Id: user.Id,
            BurnerName: burnerName,
            IsGdprAnonymized: isGdprAnonymized,
            PreferredLanguage: user.PreferredLanguage,
            FallbackPictureUrl: user.ProfilePictureUrl,
            CreatedAt: user.CreatedAt,
            LastLoginAt: user.LastLoginAt,
            LastConsentReminderSentAt: user.LastConsentReminderSentAt,
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
    /// nobodies-collective/Humans#1098: <c>User.BurnerName</c> is the sole source — the
    /// <c>Profile.BurnerName</c> / legacy <c>DisplayName</c> fallback chain is gone.
    /// The one exception is narrow tombstone recognition: a row anonymized before #1098 has
    /// a blank <c>BurnerName</c> with the erasure sentinel still sitting in <c>DisplayName</c>
    /// (the old #1097 erasure path only nulled <c>BurnerName</c>). This is NOT a general
    /// fallback — it retires once the #1102 migration drops the <c>DisplayName</c> column.
    /// </summary>
    /// <summary>
    /// A row erased via GDPR Article 17. Keyed on the <c>deleted-&lt;id&gt;@deleted.local</c>
    /// address <c>IUserRepository.ApplyExpiredDeletionAnonymizationAsync</c>
    /// mints, never on a name a member can type (nobodies-collective/Humans#1742: a member whose
    /// burner name is literally "Deleted User" was read as erased, which made
    /// <see cref="IsTombstone"/> true and dropped them out of every listing and search).
    /// Reads <see cref="User.IdentityEmailColumn"/> rather than <see cref="User.Email"/>: erasure
    /// removes the <c>UserEmail</c> rows first, so the raw Identity column is the only place the
    /// tombstone survives. Null there means a live user whose address lives only in
    /// <c>UserEmail</c> rows, never an erased one.
    ///
    /// Deliberate twin of <c>Humans.Users.Domain.UserStateEvaluator.IsGdprTombstoned</c>: that
    /// type is internal to the section and this project may not grow public surface (see the
    /// csproj), so the rule is stated twice rather than exported. Change both together.
    /// </summary>
    private static bool IsGdprTombstone(User user) =>
        (user.IdentityEmailColumn is { } email
            && email.EndsWith("@deleted.local", StringComparison.OrdinalIgnoreCase))
        // Legacy-only: rows anonymized before the email scrub existed carry the name tombstone
        // instead. The blank BurnerName is what a member cannot reproduce — erasure clears it and
        // only wrote the sentinel there from #1098 on, while a member who types these names carries
        // their burner name in every column. A guard, not the shape; see the twin's remarks.
        || (string.IsNullOrWhiteSpace(user.BurnerName)
            && string.Equals(user.DisplayName, GdprAnonymizedBurnerName, StringComparison.Ordinal)
            && string.Equals(user.FirstName, "Deleted", StringComparison.Ordinal)
            && string.Equals(user.LastName, "User", StringComparison.Ordinal));

    private static string ResolveBurnerName(string? userBurnerName, string legacyDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(userBurnerName))
            return userBurnerName;

        return string.Equals(legacyDisplayName, GdprAnonymizedBurnerName, StringComparison.Ordinal)
            ? GdprAnonymizedBurnerName
            : string.Empty;
    }
}
