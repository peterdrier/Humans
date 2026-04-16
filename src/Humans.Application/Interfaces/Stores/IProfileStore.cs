namespace Humans.Application.Interfaces.Stores;

/// <summary>
/// In-memory canonical store for <see cref="CachedProfile"/> entries,
/// keyed by <c>UserId</c>. See <c>docs/architecture/design-rules.md</c> §4
/// for the store pattern.
/// </summary>
/// <remarks>
/// Warmed at startup via <c>IProfileRepository.GetAllAsync()</c> combined
/// with <c>IUserService.GetByIdsAsync()</c>; at ~500-user scale the full
/// set fits in memory trivially. Replaces the old
/// <c>CacheKeys.Profiles</c> <c>IMemoryCache</c> entry.
/// </remarks>
public interface IProfileStore
{
    CachedProfile? GetByUserId(Guid userId);

    /// <summary>
    /// Snapshot of all cached profiles in the store.
    /// </summary>
    IReadOnlyList<CachedProfile> GetAll();

    /// <summary>
    /// Inserts or replaces a cached profile keyed by <c>userId</c>.
    /// </summary>
    void Upsert(Guid userId, CachedProfile profile);

    /// <summary>
    /// Removes a cached profile by user id. No-op if not present.
    /// </summary>
    void Remove(Guid userId);

    /// <summary>
    /// Replaces the entire contents of the store. Used by the startup
    /// warmup hosted service to populate the store once from the
    /// repositories.
    /// </summary>
    void LoadAll(IReadOnlyDictionary<Guid, CachedProfile> profiles);
}
