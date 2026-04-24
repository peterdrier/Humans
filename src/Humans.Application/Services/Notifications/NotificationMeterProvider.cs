using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Notifications;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Application-layer implementation of <see cref="INotificationMeterProvider"/>.
/// Pure aggregation + caching: discovers registered
/// <see cref="INotificationMeterContributor"/>s via DI, filters by per-user role
/// visibility, and collects their meters. Knows nothing about individual sections.
/// </summary>
/// <remarks>
/// <para>
/// This service is the push-model registry for the admin/coordinator navbar work-queue
/// badges. Each section registers one or more <see cref="INotificationMeterContributor"/>
/// instances via its DI extension (issue nobodies-collective/Humans#581). The provider
/// itself has zero knowledge of <c>IProfileService</c>, <c>IUserService</c>,
/// <c>IGoogleSyncService</c>, <c>ITeamService</c>, <c>ITicketSyncService</c>, or
/// <c>IApplicationDecisionService</c>.
/// </para>
/// <para>
/// Global-scoped contributors are cached as a cross-user bundle under
/// <see cref="CacheKeys.NotificationMeters"/> with a 2-minute TTL (§15i request-
/// acceleration cache). Writes elsewhere invalidate the bundle via
/// <see cref="INotificationMeterCacheInvalidator"/>. Per-user contributors are invoked
/// each request and self-cache if desired (e.g. board voting badge uses
/// <see cref="CacheKeys.VotingBadge"/>).
/// </para>
/// <para>
/// A failing contributor is isolated: it is logged and its meter is omitted for that
/// request, without affecting other sections' meters in the same call.
/// </para>
/// </remarks>
public sealed class NotificationMeterProvider : INotificationMeterProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sentinel principal passed to <see cref="INotificationMeterContributor.BuildMeterAsync"/>
    /// for <see cref="NotificationMeterScope.Global"/> contributors. Global contributors
    /// must not read any identity claim off this value — its contents are undefined.
    /// </summary>
    private static readonly ClaimsPrincipal GlobalScopePrincipal = new();

    private readonly IEnumerable<INotificationMeterContributor> _contributors;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NotificationMeterProvider> _logger;

    public NotificationMeterProvider(
        IEnumerable<INotificationMeterContributor> contributors,
        IMemoryCache cache,
        ILogger<NotificationMeterProvider> logger)
    {
        _contributors = contributors;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NotificationMeter>> GetMetersForUserAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var visible = _contributors.Where(c => c.IsVisibleTo(user)).ToList();
        if (visible.Count == 0)
            return [];

        // Global contributors are resolved from a shared cross-user cache bundle.
        // The cache entry holds the last-computed meter (possibly null) for each
        // global contributor key; per-user role visibility is re-applied above, so
        // two users with different roles share the same cached counts.
        Dictionary<string, NotificationMeter?>? globalBundle = null;
        if (visible.Any(c => c.Scope == NotificationMeterScope.Global))
            globalBundle = await GetCachedGlobalBundleAsync(cancellationToken);

        var perUserTasks = visible
            .Where(c => c.Scope == NotificationMeterScope.PerUser)
            .Select(c => BuildPerUserMeterAsync(c, user, cancellationToken))
            .ToList();
        var perUserResults = await Task.WhenAll(perUserTasks);

        var result = new List<NotificationMeter>(visible.Count);

        if (globalBundle is not null)
        {
            foreach (var contributor in visible.Where(c => c.Scope == NotificationMeterScope.Global))
            {
                if (globalBundle.TryGetValue(contributor.Key, out var meter) && meter is not null)
                    result.Add(meter);
            }
        }

        foreach (var meter in perUserResults)
        {
            if (meter is not null)
                result.Add(meter);
        }

        return result;
    }

    private async Task<Dictionary<string, NotificationMeter?>> GetCachedGlobalBundleAsync(
        CancellationToken cancellationToken)
    {
        var bundle = await _cache.GetOrCreateAsync(CacheKeys.NotificationMeters, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await ComputeGlobalBundleAsync(cancellationToken);
        });

        return bundle!;
    }

    private async Task<Dictionary<string, NotificationMeter?>> ComputeGlobalBundleAsync(
        CancellationToken cancellationToken)
    {
        var globalContributors = _contributors
            .Where(c => c.Scope == NotificationMeterScope.Global)
            .ToList();

        var tasks = globalContributors
            .Select(c => BuildGlobalMeterAsync(c, cancellationToken))
            .ToList();

        var results = await Task.WhenAll(tasks);

        var bundle = new Dictionary<string, NotificationMeter?>(StringComparer.Ordinal);
        foreach (var (key, meter) in results)
            bundle[key] = meter;
        return bundle;
    }

    private async Task<(string Key, NotificationMeter? Meter)> BuildGlobalMeterAsync(
        INotificationMeterContributor contributor, CancellationToken cancellationToken)
    {
        try
        {
            var meter = await contributor.BuildMeterAsync(GlobalScopePrincipal, cancellationToken);
            return (contributor.Key, meter);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Notification meter contributor {Key} (global) failed; meter omitted this cycle",
                contributor.Key);
            return (contributor.Key, null);
        }
    }

    private async Task<NotificationMeter?> BuildPerUserMeterAsync(
        INotificationMeterContributor contributor, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        try
        {
            return await contributor.BuildMeterAsync(user, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Notification meter contributor {Key} (per-user) failed; meter omitted this request",
                contributor.Key);
            return null;
        }
    }
}
