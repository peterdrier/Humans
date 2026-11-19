using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Data.Repositories;

internal partial interface IUserRepository
{
    /// <summary>Tracked; does not load aggregate-local collections.</summary>
    Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Read-only, with <see cref="Profile.VolunteerHistory"/>.</summary>
    Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every profile with <c>VolunteerHistory</c> and <c>Languages</c>, read-only.</summary>
    Task<IReadOnlyList<Profile>> GetAllProfilesAsync(CancellationToken ct = default);

    Task<Guid?> GetOwnerUserIdAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Every user whose row lacks a name their profile holds. A blank profile name never counts. Read-only.
    /// </summary>
    Task<IReadOnlyList<ProfileNameMismatch>> GetProfileNameMismatchesAsync(CancellationToken ct = default);

    /// <summary>
    /// Per user, the contact-field, language and volunteer-history rows that carry no <c>UserId</c> yet. Read-only.
    /// </summary>
    Task<IReadOnlyList<ProfileChildRowsWithoutUser>> GetProfileChildRowsWithoutUserAsync(CancellationToken ct = default);

    /// <summary>Stamps the owning user onto every such row. Returns the rows written.</summary>
    Task<int> FillProfileChildUserIdsAsync(CancellationToken ct = default);

    /// <summary>Copies the profile's non-blank names onto every mismatched user row. Returns the rows written.</summary>
    Task<int> CopyProfileNamesToUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// The content type (the GDPR gate checked before serving the file) and versioned storage key
    /// (null at the legacy key). Null when there is no picture, so an anonymized profile is never
    /// served a stale file.
    /// </summary>
    Task<(string ContentType, string? StorageKey)?> GetProfilePictureLocationAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>Ordered by proficiency descending, then language code.</summary>
    Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid profileId, CancellationToken ct = default);

    Task ReplaceLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default);

    /// <summary>Also updates the owning user's stored <see cref="UserState"/>.</summary>
    Task AddAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
    /// Saves a profile from <see cref="GetByUserIdAsync"/> and the owning user's stored
    /// <see cref="UserState"/>. A non-null CV payload is reconciled by <see cref="CVEntry.Id"/>:
    /// known ids update in place, empty or unknown ids insert, missing ids delete.
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
    /// field the account-merge fold deliberately leaves on archived profiles
    /// (medical conditions) and deletes the profile's language rows.
    /// </summary>
    Task<int> EraseProfileExtrasForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Suspends each listed user who has a profile and is not already suspended; returns the ids
    /// actually changed. The caller audits.
    /// </summary>
    Task<IReadOnlySet<Guid>> SuspendManyAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: moves the source profile's volunteer history and languages to the
    /// target and anonymizes the source profile's identifying fields in the same save.
    /// </summary>
    /// <remarks>
    /// History dedups on (year, event name); languages on code, keeping the higher proficiency
    /// (target wins ties). Contact fields are not touched. No-op if either user has no profile.
    /// </remarks>
    Task ReassignSubAggregatesToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);
}

internal sealed record ProfileNameMismatch(
    Guid UserId,
    string UserBurnerName, string UserFirstName, string UserLastName,
    string ProfileBurnerName, string ProfileFirstName, string ProfileLastName);

internal sealed record ProfileChildRowsWithoutUser(Guid UserId, int ContactFields, int Languages, int VolunteerHistory);
