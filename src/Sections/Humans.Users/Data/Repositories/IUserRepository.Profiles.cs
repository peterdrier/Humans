using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// Profile operations on <see cref="IUserRepository"/>.
/// </summary>
/// <remarks>
/// Read methods may include aggregate-local collections (<c>VolunteerHistory</c>,
/// <c>Languages</c>) where noted. The write path for CV entries is owned by
/// <see cref="UpdateAsync"/>. Language writes are handled by
/// <see cref="ReplaceLanguagesAsync"/>.
/// </remarks>
internal partial interface IUserRepository
{
    /// <summary>
    /// Loads a single profile by user id for mutation. Returns a tracked entity
    /// suitable for in-place modification followed by <see cref="UpdateAsync"/>.
    /// Does NOT eagerly load aggregate-local collections.
    /// </summary>
    Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads a read-only single profile by user id, eagerly including
    /// <see cref="Profile.VolunteerHistory"/>. Returns null if not found.
    /// Used by read-through cache paths that need the full projection.
    /// </summary>
    Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads every profile with aggregate-local <c>VolunteerHistory</c> and
    /// <c>Languages</c> collections. Used by the startup warmup hosted service
    /// to populate the profile cache. Trivial at our small scale.
    /// Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<Profile>> GetAllProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>UserId</c> for the profile with the given <paramref name="profileId"/>,
    /// or <c>null</c> if no such profile exists. Read-only scalar query.
    /// Used by services that receive a profileId from a caller but need the owning userId.
    /// </summary>
    Task<Guid?> GetOwnerUserIdAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>ProfilePictureContentType</c> column (the GDPR gate consulted by
    /// <c>ProfileService.GetProfilePictureAsync</c> before serving the on-disk file) and the
    /// versioned <c>ProfilePictureStorageKey</c> (null for pictures at the legacy deterministic key).
    /// Returns <c>null</c> if the profile does not exist or has no picture
    /// (including when the picture was cleared by an anonymization run), so
    /// an anonymized profile cannot be served a stale on-disk file. Read-only
    /// (AsNoTracking).
    /// </summary>
    Task<(string ContentType, string? StorageKey)?> GetProfilePictureLocationAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Issue nobodies-collective/Humans#702: returns every profile whose
    /// <c>ProfilePictureContentType</c> is non-null — the population the
    /// DB→FS migration verification page operates on. Projects only the
    /// scalar columns needed (no bytea load). Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<(Guid ProfileId, Guid UserId, string BurnerName, string ContentType, string? StorageKey, Instant UpdatedAt)>>
        GetCustomPictureRowsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the languages for a profile, ordered by proficiency descending
    /// then language code. Read-only.
    /// </summary>
    Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Removes all existing languages for the profile, replaces them with the
    /// given set, and persists in a single <c>SaveChangesAsync</c> call.
    /// </summary>
    Task ReplaceLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default);

    /// <summary>
    /// Persists a new profile and updates the owning user's stored <see cref="UserState"/>.
    /// </summary>
    Task AddAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to an existing profile. The provided entity is attached
    /// to a fresh context and saved. Use after mutating an entity obtained from
    /// <see cref="GetByUserIdAsync"/>. Also updates the owning user's stored
    /// <see cref="UserState"/>.
    /// A null CV payload leaves existing entries untouched.
    /// Reconciles the CV-entry collection for the given profile with the
    /// provided set, keyed by <see cref="CVEntry.Id"/>:
    /// <list type="bullet">
    ///   <item>entries with a non-empty Id that matches an existing row update
    ///     that row in place (preserving <c>Id</c> and <c>CreatedAt</c>, bumping
    ///     <c>UpdatedAt</c> only when fields actually change),</item>
    ///   <item>entries with <see cref="Guid.Empty"/> or an unknown Id are
    ///     inserted with a freshly generated Id,</item>
    ///   <item>existing rows whose Id is absent from the incoming set are
    ///     deleted.</item>
    /// </list>
    /// These changes share the profile field save.
    /// </summary>
    Task UpdateAsync(
        Profile profile, CancellationToken ct = default, IReadOnlyList<CVEntry>? volunteerHistory = null);

    /// <summary>
    /// Clears identifying profile fields, labels the profile "Deleted User", and
    /// removes its contact fields and volunteer history for GDPR erasure.
    /// </summary>
    /// <remarks>
    /// Returns false when no profile exists. Health data and languages are erased
    /// separately by <see cref="EraseProfileExtrasForUserAsync"/>.
    /// </remarks>
    Task<bool> AnonymizeForDeletionByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17, deletion path only: clears the Article 9 special-category
    /// fields the account-merge fold deliberately leaves on archived profiles
    /// (diet, allergies, intolerances, medical conditions) plus the
    /// consent-check and rejection prose, and deletes the profile's language rows.
    /// </summary>
    Task<int> EraseProfileExtrasForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.State</c> to <c>Suspended</c> for every user in
    /// <paramref name="userIds"/> that has a profile and is not already suspended.
    /// Persists in a single SaveChanges and returns the set of user ids that were
    /// actually mutated. Used by the non-compliant suspension job — the caller
    /// writes the audit log separately.
    /// </summary>
    Task<IReadOnlySet<Guid>> SuspendManyAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: bulk-moves the source profile's
    /// <see cref="VolunteerHistoryEntry"/> and <see cref="ProfileLanguage"/>
    /// rows onto the target profile, anonymizes the source profile's
    /// identifying scalar fields in the same save, and stamps
    /// <see cref="Profile.UpdatedAt"/> on the source.
    /// </summary>
    /// <remarks>
    /// Conflict rules per the fold spec:
    /// <list type="bullet">
    ///   <item><see cref="VolunteerHistoryEntry"/>: dedup on
    ///   (<see cref="VolunteerHistoryEntry.Date"/> year,
    ///   <see cref="VolunteerHistoryEntry.EventName"/>) — if target already
    ///   has the same key, drop source's row. Surviving rows are re-FK'd to
    ///   the target's <c>ProfileId</c>.</item>
    ///   <item><see cref="ProfileLanguage"/>: dedup on
    ///   <see cref="ProfileLanguage.LanguageCode"/> — keep highest
    ///   <see cref="ProfileLanguage.Proficiency"/> (target wins on tie).
    ///   Surviving source rows are re-FK'd to the target's <c>ProfileId</c>.</item>
    /// </list>
    /// The source <see cref="Profile"/> row is left in place (tombstone
    /// counterpart to <c>User.MergedToUserId</c>) but its identifying scalars
    /// (name → "Merged"/"User", picture, location, bio, emergency contact,
    /// pronouns, birthday, admin notes, contribution interests, board notes)
    /// are cleared. <see cref="ContactField"/> rows are owned by the
    /// ContactFields section (<c>IContactFieldService</c>) and are re-FK'd
    /// separately by the merge orchestrator's
    /// <c>ContactFieldService.ReassignAsync</c> call — this method does not
    /// touch <c>ContactField</c> rows.
    /// No-op if either user has no profile.
    /// </remarks>
    /// <returns>The source picture metadata after the fold commits, or null when no profiles were folded.</returns>
    Task<(Guid ProfileId, string? ContentType, string? StorageKey)?> ReassignSubAggregatesToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);
}
