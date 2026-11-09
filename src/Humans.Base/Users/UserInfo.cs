using NodaTime;

namespace Humans.Users.Contracts;

public sealed record UserEmailInfo(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsPrimary,
    string? Provider,
    string? ProviderKey,
    ContactFieldVisibility? Visibility,
    Instant? VerificationSentAt,
    Instant CreatedAt,
    Instant UpdatedAt);

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
    bool HasCustomPicture,
    string? ProfilePictureContentType,
    Instant CreatedAt,
    Instant UpdatedAt,
    string? AdminNotes,
    string? ContributionInterests,
    string? BoardNotes,
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
    IReadOnlyList<UserExternalLoginInfo> ExternalLogins,
    ProfileInfo? Profile)
{
    /// <summary>The single source of truth for access; only <see cref="UserState.Active"/> has full access.</summary>
    public UserState State { get; init; }

    /// <summary>Not a real person (the gate terminal, dev personas). Queues and counts of people skip these.</summary>
    public bool IsFake { get; init; }

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
    /// Stored <see cref="UserState.Rejected"/>. A tombstone reads its final state instead;
    /// Consent keeps the rejection record.
    /// </summary>
    public bool IsRejected => State == UserState.Rejected;

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
    /// entities are internal to <c>Humans.Users</c>. Only the owner and test fixtures build snapshots from entities.
    /// </summary>
    internal static UserInfo Create(
        User user,
        IReadOnlyList<UserEmail> userEmails,
        IReadOnlyList<(string Provider, string ProviderKey)> externalLogins,
        ProfileInfo? profile)
    {
        var userEmailInfos = userEmails
            .OrderByDescending(e => e.IsPrimary)
            .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => new UserEmailInfo(
                e.Id, e.Email, e.IsVerified, e.IsPrimary,
                e.Provider, e.ProviderKey, e.Visibility, e.VerificationSentAt,
                e.CreatedAt, e.UpdatedAt))
            .ToList();

        var loginInfos = externalLogins
            .Select(l => new UserExternalLoginInfo(l.Provider, l.ProviderKey))
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
            ExternalLogins: loginInfos,
            Profile: profile)
        {
            State = user.State,
            IsFake = user.MarkedFakeAt is not null,
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
