using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>profiles</c> and <c>profile_languages</c> tables.
/// The only non-test file that may write to those DbSets.
/// </summary>
/// <remarks>
/// Read methods may include aggregate-local collections (<c>VolunteerHistory</c>,
/// <c>Languages</c>) where noted, but the write path for those collections
/// belongs to <see cref="IVolunteerHistoryRepository"/> and the language-management
/// service respectively.
/// </remarks>
public interface IProfileRepository
{
    /// <summary>
    /// Loads a single profile by user id for mutation. Returns a tracked entity
    /// suitable for in-place modification followed by <see cref="UpdateAsync"/>.
    /// Does NOT eagerly load aggregate-local collections.
    /// </summary>
    Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads a read-only single profile by user id. Does NOT eagerly load
    /// aggregate-local collections.
    /// </summary>
    Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Batched profile fetch keyed by user id. Missing users are absent from
    /// the returned dictionary. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Loads every profile with aggregate-local <c>VolunteerHistory</c> and
    /// <c>Languages</c> collections. Used by the startup warmup hosted service
    /// to populate <see cref="Stores.IProfileStore"/>. Trivial at ~500-user scale.
    /// Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns just the profile picture data and content type.
    /// Read-only (AsNoTracking).
    /// </summary>
    Task<(byte[]? Data, string? ContentType)> GetProfilePictureDataAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Batch query returning (ProfileId, UserId, UpdatedAtTicks) for users
    /// that have a custom profile picture. Read-only.
    /// </summary>
    Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of non-suspended Colaborador and Asociado members.
    /// </summary>
    Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the languages for a profile, ordered by proficiency descending
    /// then language code. Read-only.
    /// </summary>
    Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Persists a new profile.
    /// </summary>
    Task AddAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to an existing (tracked) profile. The caller must have
    /// obtained the entity via <see cref="GetByUserIdAsync"/> in the same scope.
    /// </summary>
    Task UpdateAsync(CancellationToken ct = default);
}
