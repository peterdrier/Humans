using System.Collections.Concurrent;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Generic, thread-safe in-memory cache primitive used by the Singleton caching
/// decorators (<c>CachingProfileService</c>, <c>CachingUserService</c>,
/// <c>CachingTeamService</c>, <c>CachingShiftViewService</c>). Owns a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> and tracks hit / miss /
/// invalidation counters via <see cref="Interlocked"/>.
///
/// <para>
/// Used either as a base class (single-dict decorators inherit it) or as a
/// composed field (decorators that need multiple dicts hold N instances).
/// Either way each instance is exposed as <see cref="ICacheStats"/> on
/// <c>/Admin/CacheStats</c>.
/// </para>
/// </summary>
public class TrackedCache<TKey, TValue> : ICacheStats where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _dict = new();
    private long _hits;
    private long _misses;
    private long _invalidations;

    public string Name { get; }

    public TrackedCache(string name)
    {
        Name = name;
    }

    public int Entries => _dict.Count;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Invalidations => Interlocked.Read(ref _invalidations);

    public double HitRatePercent => Hits + Misses > 0
        ? Math.Round(Hits * 100.0 / (Hits + Misses), 1)
        : 0;

    /// <summary>
    /// Snapshot of cached values. Backed by <see cref="ConcurrentDictionary{TKey,TValue}.Values"/>
    /// — iteration is safe under concurrent mutation.
    /// </summary>
    public ICollection<TValue> Values => _dict.Values;

    /// <summary>
    /// Read-only view of the cache. Used by decorators (e.g. <c>CachingTeamService</c>)
    /// that return the cache as an <see cref="IReadOnlyDictionary{TKey, TValue}"/>
    /// for bulk consumption. Lookups via this view do <b>not</b> increment
    /// hit/miss counters — use <see cref="TryGet"/> for tracked access.
    /// </summary>
    public IReadOnlyDictionary<TKey, TValue> AsReadOnlyDictionary => _dict;

    /// <summary>
    /// True if the cache contains an entry for <paramref name="key"/>. Does not
    /// count as a hit or miss.
    /// </summary>
    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    /// <summary>
    /// Point-in-time snapshot of all key/value pairs. Safe to iterate under
    /// concurrent mutation — used by cascading-eviction paths that need to
    /// walk one cache to decide what to invalidate in another.
    /// </summary>
    public KeyValuePair<TKey, TValue>[] Snapshot() => _dict.ToArray();

    /// <summary>
    /// Cache lookup. Increments <see cref="Hits"/> on success,
    /// <see cref="Misses"/> on miss.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_dict.TryGetValue(key, out value!))
        {
            Interlocked.Increment(ref _hits);
            return true;
        }
        Interlocked.Increment(ref _misses);
        value = default!;
        return false;
    }

    /// <summary>
    /// Upsert. Does not affect counters — used by load-on-miss paths and
    /// refresh-after-write paths that have already gone through their own
    /// hit/miss accounting (or are bulk warmup).
    /// </summary>
    public void Set(TKey key, TValue value) => _dict[key] = value;

    /// <summary>
    /// Remove a single key. Increments <see cref="Invalidations"/> if an entry
    /// was actually removed.
    /// </summary>
    public bool Invalidate(TKey key)
    {
        if (_dict.TryRemove(key, out _))
        {
            Interlocked.Increment(ref _invalidations);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clear the entire dict. Always increments <see cref="Invalidations"/> by
    /// one (a wholesale flush counts as one invalidation event regardless of
    /// how many entries were present).
    /// </summary>
    public void Clear()
    {
        _dict.Clear();
        Interlocked.Increment(ref _invalidations);
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _invalidations, 0);
    }
}
