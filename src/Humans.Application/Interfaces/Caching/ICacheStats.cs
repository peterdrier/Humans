namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Diagnostic snapshot for a single in-memory caching dictionary owned by a
/// Singleton decorator (e.g. <c>CachingUserService</c>'s UserInfo dict).
/// Surfaced on <c>/Admin/CacheStats</c> alongside <see cref="ICacheStatsProvider"/>
/// (which covers <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>).
/// </summary>
public interface ICacheStats
{
    string Name { get; }
    int Entries { get; }
    long Hits { get; }
    long Misses { get; }

    /// <summary>Count of single-key removals via <c>Invalidate(key)</c> or <c>DeleteKey(key)</c>.</summary>
    long KeyRemovals { get; }

    /// <summary>Count of bulk flushes via <c>Clear()</c>. A bulk flush also
    /// flips the cache back to "cold"; the next load-all read drives a
    /// re-warm via <c>EnsureWarmed</c> / <c>EnsureWarmedAsync</c>.</summary>
    long BulkInvalidations { get; }

    double HitRatePercent { get; }
    void ResetCounters();
}
