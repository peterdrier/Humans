namespace Humans.Application.Interfaces;

/// <summary>
/// Provides cache hit/miss statistics grouped by cache key type.
/// Stats are in-memory only and reset on application restart.
/// </summary>
public interface ICacheStatsProvider
{
    IReadOnlyList<CacheStatEntry> GetSnapshot();
    void Reset();
    long TotalHits { get; }
    long TotalMisses { get; }
    int TotalActiveEntries { get; }
}

/// <summary>
/// Hit/miss statistics for a single cache key type (prefix).
/// Thread-safe: counters use Interlocked since TryGetValue is called
/// on every cache access across the entire app.
/// </summary>
public sealed class CacheStatEntry
{
    public string KeyType { get; }

    private long _hits;
    private long _misses;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public double HitRatePercent => Hits + Misses > 0
        ? Math.Round(Hits * 100.0 / (Hits + Misses), 1)
        : 0;

    public CacheStatEntry(string keyType, long hits, long misses)
    {
        KeyType = keyType;
        _hits = hits;
        _misses = misses;
    }

    public CacheStatEntry RecordHit()
    {
        Interlocked.Increment(ref _hits);
        return this;
    }

    public CacheStatEntry RecordMiss()
    {
        Interlocked.Increment(ref _misses);
        return this;
    }
}
