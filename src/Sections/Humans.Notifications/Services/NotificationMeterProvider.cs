using Humans.Base.Extensions;
using System.Security.Claims;
using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;

namespace Humans.Notifications.Services;

/// <summary>
/// Collects every section's meters for the viewer. Each count and its caching stay with the owning section.
/// </summary>
internal sealed class NotificationMeterProvider(
    IEnumerable<INotificationMeterContributor> contributors,
    ILogger<NotificationMeterProvider> logger) : IOrchestrator
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersForUserAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default, Guid? resolvedUserId = null)
    {
        var viewerId = resolvedUserId;
        if (viewerId is null && user.GetUserId() is { } claimedUserId)
            viewerId = claimedUserId;
        if (viewerId is not { } id)
            return [];

        var meters = new List<NotificationMeter>();
        foreach (var contributor in contributors)
        {
            try
            {
                var contributed = await contributor.GetMetersAsync(user, id, cancellationToken);
                meters.AddRange(contributed.ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Notification meter contributor {Contributor} failed for user {UserId}; its meters are skipped",
                    contributor.GetType().Name, id);
            }
        }

        return meters;
    }
}
