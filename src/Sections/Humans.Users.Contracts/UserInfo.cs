using NodaTime;

namespace Humans.Users.Contracts;

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
    Instant UpdatedAt,
    GoogleEmailStatus GoogleEmailStatus);

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
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
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
    string? Iban,
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
    Instant? LastConsentReminderSentAt,
    Instant? DeletionRequestedAt,
    Instant? DeletionScheduledFor,
    Instant? DeletionEligibleAfter,
    bool UnsubscribedFromCampaigns,
    bool SuppressScheduleChangeEmails,
    Instant? MagicLinkSentAt,
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

    /// <summary>Account was merged into another account — the row is a merge-source tombstone.</summary>
    public bool IsMerged => MergedAt is not null;

    /// <summary>
    /// Sentinel value written to the legacy User.DisplayName column by
    /// <see cref="Humans.Base.Interfaces.Repositories.IUserRepository.ApplyExpiredDeletionAnonymizationAsync"/>
    /// to mark GDPR-deleted users. Read into <see cref="IsGdprAnonymized"/>
    /// at creation time so the legacy name never becomes a public UserInfo field.
    /// </summary>
    public const string GdprAnonymizedBurnerName = UserStateClassifier.GdprAnonymizedDisplayName;

    /// <summary>
    /// True when the user row is a tombstone — a merge-source
    /// (<see cref="MergedAt"/> set), a GDPR-anonymized record (legacy User.DisplayName
    /// resolved into <see cref="BurnerName"/> as <see cref="GdprAnonymizedBurnerName"/> by
    /// <see cref="Humans.Base.Interfaces.Repositories.IUserRepository.ApplyExpiredDeletionAnonymizationAsync"/>),
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

    /// <summary>First verified IsGoogle UserEmail.</summary>
    public string? GoogleEmail => UserEmails
        .Where(e => e.IsGoogle && e.IsVerified)
        .Select(e => e.Email)
        .FirstOrDefault();

    /// <summary>
    /// Effective Google Workspace sync status for this user — the per-address status of the
    /// address sync actually targets. That target mirrors
    /// <c>GoogleWorkspaceSyncService.TryGetGoogleEmail</c>: the verified
    /// <see cref="UserEmailInfo.IsGoogle"/> row, else the verified provider (OAuth) fallback row
    /// (covers ~pre-#687 users with no IsGoogle row). <see cref="GoogleEmailStatus.Unknown"/>
    /// when there is no such address. Replaces the deprecated user-level <c>User.GoogleEmailStatus</c>
    /// column (nobodies-collective/Humans#687); a rejection no longer survives switching Google address.
    /// </summary>
    public GoogleEmailStatus GoogleEmailStatus
    {
        get
        {
            var target = UserEmails.FirstOrDefault(e => e.IsGoogle && e.IsVerified)
                ?? UserEmails
                    .Where(e => e.IsVerified && e.Provider != null)
                    .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            return target?.GoogleEmailStatus ?? GoogleEmailStatus.Unknown;
        }
    }

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

    /// <summary>Stub: hasn't entered their name yet (<see cref="UserState.Bare"/>) — no profile row
    /// or blank required names. Callers writing consents must block on this.</summary>
    public bool IsStub => State == UserState.Bare;

    /// <summary>Has a profile, is not rejected, and is not a merge/deletion tombstone (a tombstone
    /// keeps its anonymized Profile row, so that check alone isn't enough — peterdrier/Humans#1707).
    /// Does NOT require <see cref="ProfileInfo.IsApproved"/> (separate Consent Coordinator gate).
    /// Reads rejection off the stored <see cref="State"/>.</summary>
    public bool IsActive =>
        Profile is not null && State != UserState.Rejected && !IsTombstone;

    /// <summary>Canonical "suspended" predicate — see memory/code/no-issuspended.md. Derives from the
    /// stored <see cref="State"/>, which is where suspension itself lives.</summary>
    public bool IsSuspended =>
        State is UserState.Suspended or UserState.AdminSuspended;

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

    /// <summary>In CC review queue: active (which excludes merged/deleted tombstones), named, and not
    /// yet approved. Shared by queue list + nav badge + admin dashboard so they cannot drift.</summary>
    public bool NeedsConsentReview =>
        IsActive && HasRequiredNameFields && !Profile!.IsApproved;

    /// <summary>
    /// Carries an unresolved Flagged consent check. Excludes rejected profiles — those have already
    /// been dealt with and the Clear mutation is blocked, so they'd be unresolvable in the queue —
    /// and merged/deleted tombstones, which are not live accounts left to review.
    /// Drives the /OnboardingReview flagged section.
    /// </summary>
    public bool IsConsentCheckFlagged =>
        Profile?.ConsentCheckStatus == ConsentCheckStatus.Flagged
        && Profile.RejectedAt is null
        && !IsTombstone;

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
        IReadOnlyList<CommunicationPreferenceInfo> communicationPreferences)
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
            .Select(p => new EventParticipationInfo(
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
            UnsubscribedFromCampaigns: user.UnsubscribedFromCampaigns,
            SuppressScheduleChangeEmails: user.SuppressScheduleChangeEmails,
            MagicLinkSentAt: user.MagicLinkSentAt,
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
    /// <c>CachingUserService.ResolveBurnerName</c> is the cache-refresh twin of this.
    /// </summary>
    /// <summary>
    /// A row erased via GDPR Article 17. Keyed on the <c>deleted-&lt;id&gt;@deleted.local</c>
    /// address <see cref="Humans.Base.Interfaces.Repositories.IUserRepository.ApplyExpiredDeletionAnonymizationAsync"/>
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
