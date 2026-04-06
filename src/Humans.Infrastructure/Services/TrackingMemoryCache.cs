using System.Collections.Concurrent;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Decorator around <see cref="IMemoryCache"/> that tracks hit/miss statistics
/// per cache key type. Registered as the primary IMemoryCache in DI so all
/// existing consumers get automatic tracking with no code changes.
/// Stats are in-memory only — reset on application restart.
/// </summary>
public sealed class TrackingMemoryCache : IMemoryCache, ICacheStatsProvider
{
    private readonly IMemoryCache _inner;
    private readonly ConcurrentDictionary<string, CacheStatEntry> _stats = new(StringComparer.Ordinal);

    public TrackingMemoryCache(IMemoryCache inner)
    {
        _inner = inner;
    }

    public bool TryGetValue(object key, out object? value)
    {
        var keyType = DeriveKeyType(key);
        var found = _inner.TryGetValue(key, out value);

        if (found)
        {
            _stats.AddOrUpdate(
                keyType,
                _ => new CacheStatEntry(keyType, 1, 0),
                (_, existing) => existing.RecordHit());
        }
        else
        {
            _stats.AddOrUpdate(
                keyType,
                _ => new CacheStatEntry(keyType, 0, 1),
                (_, existing) => existing.RecordMiss());
        }

        return found;
    }

    public ICacheEntry CreateEntry(object key)
    {
        return _inner.CreateEntry(key);
    }

    public void Remove(object key)
    {
        _inner.Remove(key);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    // ICacheStatsProvider

    public IReadOnlyList<CacheStatEntry> GetSnapshot()
    {
        return _stats.Values
            .OrderByDescending(e => e.Hits + e.Misses)
            .ToList();
    }

    public void Reset()
    {
        _stats.Clear();
    }

    public long TotalHits => _stats.Values.Sum(e => e.Hits);
    public long TotalMisses => _stats.Values.Sum(e => e.Misses);

    /// <summary>
    /// Derive a human-readable "type" from the cache key.
    /// Keys follow the pattern "Prefix:id" — we extract the prefix.
    /// Simple keys without a colon use the full key as the type.
    /// </summary>
    private static string DeriveKeyType(object key)
    {
        var keyStr = key.ToString() ?? "(null)";
        var colonIndex = keyStr.IndexOf(':');
        return colonIndex > 0 ? keyStr[..colonIndex] : keyStr;
    }
}
