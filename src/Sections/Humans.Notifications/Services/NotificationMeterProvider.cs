using Humans.Base.Extensions;
using System.Security.Claims;
using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;

namespace Humans.Notifications.Services;

/// <summary>
/// Collects every section's meters for the viewer. Each count and its caching stay with the owning section. A
/// section switched off for the event contributes none: its work queue lives on pages that answer 404.
/// </summary>
internal sealed class NotificationMeterProvider(
    IEnumerable<INotificationMeterContributor> contributors,
    ILogger<NotificationMeterProvider> logger,
    ISectionSwitches? switches = null) : IOrchestrator
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersForUserAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default, Guid? resolvedUserId = null)
    {
        var viewerId = resolvedUserId;
        if (viewerId is null && user.GetUserId() is { } claimedUserId)
            viewerId = claimedUserId;
        if (viewerId is not { } id)
            return [];

        var switchedOff = switches is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await switches.GetSwitchedOffAsync(cancellationToken);
        var meters = new List<NotificationMeter>();
        foreach (var contributor in contributors
            .Where(c => !switchedOff.Contains(SectionSwitchboard.SectionNameOf(c.GetType()))))
        {
            try
            {
                var contributed = await contributor.GetMetersAsync(user, id, cancellationToken);
                meters.AddRange(contributed.ToList());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification meter contributor {Contributor} failed for user {UserId}; its meters are skipped",
                    contributor.GetType().Name, id);
            }
        }

        return meters;
    }
}
