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
    /// <summary>Evict one user's cached EE (member-level grant/signup change).</summary>
    void InvalidateUser(Guid userId);

    /// <summary>
    /// Evict the whole cache. For global config changes that shift every holder's
    /// EE at once — the camps' global <c>EeStartDate</c> and EventSettings gate /
    /// build-offset edits (which move every shift-derived date).
    /// </summary>
    void InvalidateAll();
}
