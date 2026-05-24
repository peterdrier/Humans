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

    public static void InvalidateNotificationMeters(this IMemoryCache cache) =>
        cache.Remove(CacheKeys.NotificationMeters);

    public static void InvalidateActiveTeams(this IMemoryCache cache)
    {
        cache.Remove(CacheKeys.ActiveTeams);
    }

    // Camp-cache invalidation extensions were retired in T-06 — eviction
    // is now owned by CachingCampService (Infrastructure decorator) and
    // reached through ICampInfoInvalidator. The CampSeasonsByYear /
    // CampSettings keys are gone from CacheKeys as well (snapshot lives on
    // the decorator).
    //
    // Ticket-cache invalidation extensions were retired in T-07. Eviction is
    // now owned by CachingTicketQueryService (Infrastructure decorator) and
    // reached through ITicketCacheInvalidator. Per-user holdings live in that
    // decorator's TrackedCache; the only remaining IMemoryCache ticket key the
    // decorator removes directly is TicketEventSummary.

    public static void InvalidateCampContactRateLimit(this IMemoryCache cache, Guid userId, Guid campId) =>
        cache.Remove(CacheKeys.CampContactRateLimit(userId, campId));

    public static void InvalidateRoleAssignmentClaims(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.RoleAssignmentClaims(userId));

    public static void InvalidateShiftAuthorization(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.ShiftAuthorization(userId));

    public static void InvalidateVotingBadge(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.VotingBadge(userId));

    public static void InvalidateIssuesBadge(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.IssuesBadge(userId));

    public static void InvalidateCampLeadJoinRequestsBadge(this IMemoryCache cache, Guid userId) =>
        cache.Remove(CacheKeys.CampLeadJoinRequestsBadge(userId));

    public static void InvalidateUserAccess(this IMemoryCache cache, Guid userId)
    {
        cache.InvalidateActiveTeams();
        cache.InvalidateRoleAssignmentClaims(userId);
        cache.InvalidateShiftAuthorization(userId);
    }

}
