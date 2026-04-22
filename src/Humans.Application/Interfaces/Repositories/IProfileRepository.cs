using Humans.Domain.Entities;
using Humans.Application;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>profiles</c> and <c>profile_languages</c> tables.
/// The only non-test file that may write to those DbSets.
/// </summary>
/// <remarks>
/// Read methods may include aggregate-local collections (<c>VolunteerHistory</c>,
/// <c>Languages</c>) where noted. The write path for CV entries is owned by
/// <see cref="ReconcileCVEntriesAsync"/>. Language writes are handled by
/// <see cref="ReplaceLanguagesAsync"/>.
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
    /// Loads a read-only single profile by user id, eagerly including
    /// <see cref="Profile.VolunteerHistory"/>. Returns null if not found.
    /// Used by read-through cache paths that need the full projection.
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
    /// to populate the profile cache. Trivial at ~500-user scale.
    /// Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>UserId</c> for the profile with the given <paramref name="profileId"/>,
    /// or <c>null</c> if no such profile exists. Read-only scalar query.
    /// Used by services that receive a profileId from a caller but need the owning userId.
    /// </summary>
    Task<Guid?> GetOwnerUserIdAsync(Guid profileId, CancellationToken ct = default);

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
    /// Returns the user ids of all profiles that are approved and not
    /// suspended. Read-only (AsNoTracking). Used by notification fan-out
    /// that previously read <c>_dbContext.Profiles</c> directly from
    /// cross-section services (e.g. Legal document sync).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveApprovedUserIdsAsync(CancellationToken ct = default);

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
    /// Persists a new profile.
    /// </summary>
    Task AddAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to an existing profile. The provided entity is attached
    /// to a fresh context and saved. Use after mutating an entity obtained from
    /// <see cref="GetByUserIdAsync"/>.
    /// </summary>
    Task UpdateAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
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
    /// Persists in a single SaveChanges.
    /// </summary>
    Task ReconcileCVEntriesAsync(
        Guid profileId,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct = default);
}
