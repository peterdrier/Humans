using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Humans.Application.Interfaces;
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

    public static void InvalidateNotificationMeters(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.NotificationMeters);

    public static void InvalidateApprovedProfiles(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.ApprovedProfiles);

    public static void InvalidateActiveTeams(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.ActiveTeams);

    public static void InvalidateCampSeasonsByYear(this IMemoryCache cache, int year) =>
        cache.Remove(CacheKeys.CampSeasonsByYear(year));

    public static void InvalidateCampSettings(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.CampSettings);

    public static void InvalidateCampContactRateLimit(this IMemoryCache cache, Guid userId, Guid campId) =>
        cache.Remove(CacheKeys.CampContactRateLimit(userId, campId));

    public static void InvalidateRoleAssignmentClaims(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.RoleAssignmentClaims(userId));

    public static void InvalidateShiftAuthorization(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.ShiftAuthorization(userId));

    public static void InvalidateUserAccess(this IMemoryCache cache, Guid userId)
    {
        cache.InvalidateActiveTeams();
        cache.InvalidateRoleAssignmentClaims(userId);
        cache.InvalidateShiftAuthorization(userId);
    }

    public static void SetApprovedProfile(this IMemoryCache cache, Guid userId, CachedProfile profile) =>
        cache.TryUpdateExistingValue<ConcurrentDictionary<Guid, CachedProfile>>(CacheKeys.ApprovedProfiles, cached =>
            cached[userId] = profile);

    public static void RemoveApprovedProfile(this IMemoryCache cache, Guid userId) =>
        cache.TryUpdateExistingValue<ConcurrentDictionary<Guid, CachedProfile>>(CacheKeys.ApprovedProfiles, cached =>
            cached.TryRemove(userId, out _));

    public static void UpdateApprovedProfile(this IMemoryCache cache, Guid userId, CachedProfile? profile)
    {
        if (profile is not null)
        {
            cache.SetApprovedProfile(userId, profile);
            return;
        }

        cache.RemoveApprovedProfile(userId);
    }
}
