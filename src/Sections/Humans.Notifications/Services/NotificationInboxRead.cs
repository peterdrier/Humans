using System.Security.Claims;
using Humans.Notifications.Contracts;

namespace Humans.Notifications.Services;

/// <summary>
/// Adapter behind <see cref="INotificationInboxRead"/>: composes the unread tab of
/// <see cref="INotificationInboxService"/> with <see cref="NotificationMeterProvider"/>
/// into the one snapshot the machine API serves. Same-section composition only; it owns
/// nothing and touches no repository.
/// </summary>
internal sealed class NotificationInboxRead(
    INotificationInboxService inbox,
    NotificationMeterProvider meterProvider) : INotificationInboxRead
{
    public async Task<NotificationInboxSnapshot> GetUnreadInboxAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
    {
        IReadOnlyList<UnreadNotificationDto> notifications = [];
        if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var result = await inbox.GetInboxAsync(userId, search: null, filter: "all", tab: "unread", ct);
            notifications = result.NeedsAttention
                .Concat(result.Informational)
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => new UnreadNotificationDto(
                    n.CreatedAt, n.Source, n.Title, n.ActionUrl, n.Priority, n.Class, !n.IsRead))
                .ToList();
        }

        var meters = await meterProvider.GetMetersForUserAsync(user, ct);
        return new NotificationInboxSnapshot(
            notifications,
            meters
                .OrderByDescending(m => m.Priority)
                .Select(m => new NotificationMeterDto(m.Title, m.Count, m.ActionUrl))
                .ToList());
    }
}
