using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Persists a notification to a pre-resolved list of recipient user IDs. Has no
/// dependency on <see cref="INotificationRecipientResolver"/>, so
/// <see cref="ITeamService"/> and <see cref="IRoleAssignmentService"/> can inject
/// this without closing a DI cycle through <see cref="INotificationService"/>.
/// <see cref="NotificationService"/> delegates here so dispatch logic lives in one place.
/// </summary>
public sealed class NotificationEmitter(
    INotificationRepository repo,
    ICommunicationPreferenceService preferenceService,
    IClock clock,
    IMemoryCache cache,
    ILogger<NotificationEmitter> logger) : INotificationEmitter
{
    public async Task SendAsync(
        NotificationSource source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        IReadOnlyList<Guid> recipientUserIds,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        string? targetGroupName = null,
        CancellationToken cancellationToken = default)
    {
        if (recipientUserIds.Count == 0)
        {
            logger.LogWarning("SendAsync called with empty recipient list for source {Source}, title '{Title}'",
                source, title);
            return;
        }

        var now = clock.GetCurrentInstant();
        var category = source.ToMessageCategory();

        var inboxDisabled = await preferenceService.GetUsersWithInboxDisabledAsync(
            recipientUserIds, category, cancellationToken);

        var notifications = new List<Notification>(recipientUserIds.Count);
        foreach (var userId in recipientUserIds)
        {
            if (notificationClass == NotificationClass.Informational && inboxDisabled.Contains(userId))
            {
                logger.LogDebug(
                    "Skipping informational notification for user {UserId} — InboxEnabled=false for {Category}",
                    userId, category);
                continue;
            }

            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                Title = title,
                Body = body,
                ActionUrl = actionUrl,
                ActionLabel = actionLabel,
                Priority = priority,
                Source = source,
                Class = notificationClass,
                TargetGroupName = targetGroupName,
                CreatedAt = now,
            };

            notification.Recipients.Add(new NotificationRecipient
            {
                NotificationId = notification.Id,
                UserId = userId,
            });

            notifications.Add(notification);
        }

        if (notifications.Count == 0)
        {
            logger.LogInformation(
                "SendAsync: all {Count} recipient(s) suppressed notification for source {Source}",
                recipientUserIds.Count, source);
            return;
        }

        await repo.AddRangeAsync(notifications, cancellationToken);
        foreach (var n in notifications)
        {
            cache.Remove(CacheKeys.NotificationBadgeCounts(n.Recipients.Single().UserId));
        }

        logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to {Count} individual recipient(s)",
            source, title, notifications.Count);
    }
}
