using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Application.Extensions;

public static class MemoryCacheExtensions
{
    public static bool TryGetExistingValue<TValue>(
        this IMemoryCache cache,
        object key,
        [NotNullWhen(true)] out TValue? value)
    {
        if (cache.TryGetValue(key, out var cached) && cached is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public static async Task<bool> TryReserveAsync(
        this IMemoryCache cache,
        object key,
        TimeSpan absoluteExpirationRelativeToNow)
    {
        var created = false;

        await cache.GetOrCreateAsync(key, entry =>
        {
            created = true;
            entry.AbsoluteExpirationRelativeToNow = absoluteExpirationRelativeToNow;
            return Task.FromResult(true);
        });

        return created;
    }

    public static bool TryUpdateExistingValue<TValue>(
        this IMemoryCache cache,
        object key,
        Action<TValue> update)
    {
        if (!cache.TryGetExistingValue(key, out TValue? value))
        {
            return false;
        }

        update(value);
        return true;
    }

    public static void InvalidateNavBadgeCounts(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.NavBadgeCounts);

    public static void InvalidateApprovedProfiles(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.ApprovedProfiles);

    public static void InvalidateActiveTeams(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.ActiveTeams);

    public static void InvalidateCampSeasonsByYear(this IMemoryCache cache, int year) =>
        cache.Remove(CacheKeys.CampSeasonsByYear(year));
}
