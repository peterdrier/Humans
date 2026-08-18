using Humans.Auth.Contracts;
using Humans.Application;
using Humans.Notifications.Data;
using Humans.Notifications.Domain;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.Notifications.Services;

/// <summary>
/// Persists a notification to a pre-resolved list of recipient user IDs. Has no
/// dependency on <see cref="INotificationRecipientResolver"/>, so
/// <see cref="ITeamService"/> and <see cref="IRoleAssignmentService"/> can inject
/// this without closing a DI cycle through <see cref="INotificationService"/>.
/// <see cref="NotificationService"/> delegates here so dispatch logic lives in one place.
/// </summary>
internal sealed class NotificationEmitter(
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
        string? sourceKey = null,
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
                SourceKey = sourceKey,
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
