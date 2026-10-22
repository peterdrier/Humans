using Humans.Email.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Caching;
using Humans.Notifications.Data;
using Humans.Notifications.Domain;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Teams.Contracts;

namespace Humans.Notifications.Services;

/// <summary>Must not inject ITeamService or IRoleAssignmentService: both inject this.</summary>
internal sealed class NotificationEmitter(
    INotificationRepository repo,
    ICommunicationPreferenceService preferenceService,
    IClock clock,
    IMemoryCache cache,
    ILogger<NotificationEmitter> logger) : INotificationEmitter
{
    internal static (string Title, string? Body) FitCopyToStorage(string title, string? body)
    {
        if (title.EnumerateRunes().Count() > Notification.TitleMaxLength)
        {
            body = body is null ? title : string.Concat(title, "\n\n", body);
            title = string.Concat(title.EnumerateRunes().Take(Notification.TitleMaxLength - 1)) + "…";
        }
        if (body is not null && body.EnumerateRunes().Count() > Notification.BodyMaxLength)
            body = string.Concat(body.EnumerateRunes().Take(Notification.BodyMaxLength - 1)) + "…";
        return (title, body);
    }

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

        var inboxDisabled = notificationClass == NotificationClass.Informational
            ? await preferenceService.GetUsersWithInboxDisabledAsync(recipientUserIds, category, cancellationToken)
            : null;

        (title, body) = FitCopyToStorage(title, body);
        var notifications = new List<Notification>(recipientUserIds.Count);
        foreach (var userId in recipientUserIds)
        {
            if (inboxDisabled?.Contains(userId) == true)
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
