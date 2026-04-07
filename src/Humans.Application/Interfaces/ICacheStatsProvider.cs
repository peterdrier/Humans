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
}

/// <summary>
/// Hit/miss statistics for a single cache key type (prefix).
/// </summary>
public sealed class CacheStatEntry
{
    public string KeyType { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public double HitRatePercent => Hits + Misses > 0
        ? Math.Round(Hits * 100.0 / (Hits + Misses), 1)
        : 0;

    public CacheStatEntry(string keyType, long hits, long misses)
    {
        KeyType = keyType;
        Hits = hits;
        Misses = misses;
    }

    public CacheStatEntry RecordHit()
    {
        Hits++;
        return this;
    }

    public CacheStatEntry RecordMiss()
    {
        Misses++;
        return this;
    }
}
