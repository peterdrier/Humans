namespace Humans.Application.Interfaces.EarlyEntry;

/// <summary>
/// §15e one-way cache-staleness signal for the per-user EE cache. Implemented by
/// the caching decorator. EE is derived from camp grants and build-shift signups,
/// so the Camps and Shifts write paths inject this and evict the affected user
/// after their writes. Pure eviction (the cache has no warmup); the next read
/// lazy-reloads.
/// </summary>
public interface IEarlyEntryInvalidator
{
    void InvalidateUser(Guid userId);
}
