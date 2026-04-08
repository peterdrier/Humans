using System.Collections.Concurrent;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Decorator around <see cref="IMemoryCache"/> that tracks hit/miss statistics
/// per cache key type and active entry counts. Registered as the primary
/// IMemoryCache in DI so all existing consumers get automatic tracking with
/// no code changes. Stats are in-memory only — reset on application restart.
/// </summary>
public sealed class TrackingMemoryCache : IMemoryCache, ICacheStatsProvider
{
    private readonly IMemoryCache _inner;
    private readonly ConcurrentDictionary<string, CacheStatEntry> _stats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _liveKeys = new(StringComparer.Ordinal);

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
        var keyStr = key.ToString() ?? "(null)";
        _liveKeys.TryAdd(keyStr, 0);

        var entry = _inner.CreateEntry(key);
        entry.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            var evictedKeyStr = evictedKey.ToString() ?? "(null)";
            _liveKeys.TryRemove(evictedKeyStr, out _);
        });

        return entry;
    }

    public void Remove(object key)
    {
        _inner.Remove(key);
        var keyStr = key.ToString() ?? "(null)";
        _liveKeys.TryRemove(keyStr, out _);
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

    public int TotalActiveEntries => _liveKeys.Count;

    /// <summary>
    /// Gets the number of active cache entries per key type (prefix).
    /// </summary>
    public IReadOnlyDictionary<string, int> GetActiveEntryCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in _liveKeys.Keys)
        {
            var keyType = DeriveKeyType(key);
            counts.TryGetValue(keyType, out var current);
            counts[keyType] = current + 1;
        }
        return counts;
    }

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
