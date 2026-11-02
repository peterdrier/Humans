using Humans.Email.Contracts;
using Humans.Notifications.Data;
using Humans.Notifications.Domain;
using Humans.Notifications.Contracts;
using NodaTime;

namespace Humans.Notifications.Services;

/// <summary>Must not inject ITeamService or IRoleAssignmentService: both inject this.</summary>
internal sealed class NotificationEmitter(
    INotificationRepository repo,
    ICommunicationPreferenceService preferenceService,
    IClock clock,
    ILogger<NotificationEmitter> logger) : INotificationEmitter
{
    private static (string Title, string? Body) FitCopyToStorage(string title, string? body)
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

    public Task SendAsync(
        NotificationSourceDefinition source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        IReadOnlyList<Guid> recipientUserIds,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        string? targetGroupName = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(source, notificationClass, priority, title, recipientUserIds, body, actionUrl,
            actionLabel, targetGroupName, sourceKey, sharedAcrossRecipients: false, cancellationToken);

    internal async Task SendAsync(
        NotificationSourceDefinition source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        IReadOnlyList<Guid> recipientUserIds,
        string? body,
        string? actionUrl,
        string? actionLabel,
        string? targetGroupName,
        string? sourceKey,
        bool sharedAcrossRecipients,
        CancellationToken cancellationToken)
    {
        if (recipientUserIds.Count == 0)
        {
            logger.LogWarning("SendAsync called with empty recipient list for source {Source}, title '{Title}'",
                source, title);
            return;
        }

        var now = clock.GetCurrentInstant();
        var category = source.Category;

        var inboxDisabled = notificationClass == NotificationClass.Informational
            ? await preferenceService.GetUsersWithInboxDisabledAsync(recipientUserIds, category, cancellationToken)
            : null;

        (title, body) = FitCopyToStorage(title, body);
        var recipients = recipientUserIds.Where(userId => inboxDisabled?.Contains(userId) != true).ToArray();
        if (recipients.Length == 0)
        {
            logger.LogInformation(
                "SendAsync: all {Count} recipient(s) suppressed notification for source {Source}",
                recipientUserIds.Count, source);
            return;
        }

        IEnumerable<IEnumerable<Guid>> recipientGroups = sharedAcrossRecipients
            ? [recipients]
            : recipients.Select(userId => new[] { userId });
        var notifications = new List<Notification>();
        foreach (var group in recipientGroups)
        {
            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                Title = title,
                Body = body,
                ActionUrl = actionUrl,
                ActionLabel = actionLabel,
                Priority = priority,
                Source = source.Name,
                SourceKey = sourceKey,
                Class = notificationClass,
                TargetGroupName = targetGroupName,
                CreatedAt = now,
            };

            foreach (var userId in group)
            {
                notification.Recipients.Add(new NotificationRecipient
                {
                    NotificationId = notification.Id,
                    UserId = userId,
                });
            }

            notifications.Add(notification);
        }

        await repo.AddRangeAsync(notifications, cancellationToken);

        logger.LogInformation(
            "Dispatched {Source} notification '{Title}' as {Count} notification(s) to {RecipientCount} recipient(s)",
            source, title, notifications.Count, recipients.Length);
    }
}
