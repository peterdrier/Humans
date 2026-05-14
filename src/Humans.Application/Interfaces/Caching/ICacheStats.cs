namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Diagnostic snapshot for a single in-memory caching dictionary owned by a
/// Singleton decorator (e.g. <c>CachingProfileService</c>'s FullProfile dict).
/// Surfaced on <c>/Admin/CacheStats</c> alongside <see cref="ICacheStatsProvider"/>
/// (which covers <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>).
/// </summary>
public interface ICacheStats
{
    string Name { get; }
    int Entries { get; }
    long Hits { get; }
    long Misses { get; }
    long Invalidations { get; }
    double HitRatePercent { get; }
    void ResetCounters();
}
