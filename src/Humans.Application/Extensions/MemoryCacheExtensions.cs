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

    public static void InvalidateProfiles(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.Profiles);

    public static void InvalidateActiveTeams(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.ActiveTeams);

    public static void InvalidateCampSeasonsByYear(this IMemoryCache cache, int year) =>
        cache.Remove(CacheKeys.CampSeasonsByYear(year));

    public static void InvalidateCampSettings(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.CampSettings);

    public static void InvalidateUserTicketCount(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.UserTicketCount(userId));

    public static void InvalidateTicketDashboardStats(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.TicketDashboardStats);

    public static void InvalidateUserIdsWithTickets(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.UserIdsWithTickets);

    /// <summary>
    /// Invalidate all ticket-related caches after a sync or data change.
    /// Per-user UserTicketCount entries are NOT invalidated here because they use
    /// per-user keys that can't be enumerated for bulk invalidation. They expire
    /// naturally via their 5-minute TTL, which is acceptable at ~500-user scale.
    /// </summary>
    public static void InvalidateTicketCaches(this IMemoryCache cache)
    {
        cache.InvalidateTicketDashboardStats();
        cache.InvalidateUserIdsWithTickets();
    }

    public static void InvalidateUserProfile(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.UserProfile(userId));

    public static void InvalidateCampContactRateLimit(this IMemoryCache cache, Guid userId, Guid campId) =>
        cache.Remove(CacheKeys.CampContactRateLimit(userId, campId));

    public static void InvalidateRoleAssignmentClaims(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.RoleAssignmentClaims(userId));

    public static void InvalidateShiftAuthorization(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.ShiftAuthorization(userId));

    public static void InvalidateVotingBadge(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.VotingBadge(userId));

    public static void InvalidateUserAccess(this IMemoryCache cache, Guid userId)
    {
        cache.InvalidateActiveTeams();
        cache.InvalidateRoleAssignmentClaims(userId);
        cache.InvalidateShiftAuthorization(userId);
    }

    public static void SetProfile(this IMemoryCache cache, Guid userId, CachedProfile profile) =>
        cache.TryUpdateExistingValue<ConcurrentDictionary<Guid, CachedProfile>>(CacheKeys.Profiles, cached =>
            cached[userId] = profile);

    public static void RemoveProfile(this IMemoryCache cache, Guid userId) =>
        cache.TryUpdateExistingValue<ConcurrentDictionary<Guid, CachedProfile>>(CacheKeys.Profiles, cached =>
            cached.TryRemove(userId, out _));

    public static void UpdateProfile(this IMemoryCache cache, Guid userId, CachedProfile? profile)
    {
        if (profile is not null)
        {
            cache.SetProfile(userId, profile);
            return;
        }

        cache.RemoveProfile(userId);
    }
}
