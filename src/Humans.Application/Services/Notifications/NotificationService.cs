using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Application-layer implementation of <see cref="INotificationService"/>.
/// Dispatches in-app notifications, materializes recipients, checks
/// preferences, and delegates persistence to <see cref="INotificationRepository"/>.
/// Invalidates per-user nav-badge cache keys after every successful send (§15 Option A).
/// </summary>
public sealed class NotificationService(
    INotificationEmitter emitter,
    INotificationRepository repo,
    INotificationRecipientResolver recipientResolver,
    ICommunicationPreferenceService preferenceService,
    IClock clock,
    IMemoryCache cache,
    ILogger<NotificationService> logger) : INotificationService, IUserMerge
{
    public Task SendAsync(
        NotificationSource source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        IReadOnlyList<Guid> recipientUserIds,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        string? targetGroupName = null,
        CancellationToken cancellationToken = default) =>
        emitter.SendAsync(
            source, notificationClass, priority, title, recipientUserIds,
            body, actionUrl, actionLabel, targetGroupName, cancellationToken);

    public async Task SendToTeamAsync(
        NotificationSource source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        Guid teamId,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        CancellationToken cancellationToken = default)
    {
        var team = await recipientResolver.GetTeamNotificationInfoAsync(teamId, cancellationToken);
        if (team is null)
        {
            logger.LogWarning("SendToTeamAsync: team {TeamId} not found", teamId);
            return;
        }

        var memberUserIds = team.MemberUserIds;
        if (memberUserIds.Count == 0)
        {
            logger.LogWarning("SendToTeamAsync: team {TeamId} has no members", teamId);
            return;
        }

        var now = clock.GetCurrentInstant();
        var category = source.ToMessageCategory();

        var inboxDisabled = await preferenceService.GetUsersWithInboxDisabledAsync(
            memberUserIds, category, cancellationToken);

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
            TargetGroupName = team.Name,
            CreatedAt = now,
        };

        foreach (var userId in memberUserIds)
        {
            if (notificationClass == NotificationClass.Informational && inboxDisabled.Contains(userId))
            {
                continue;
            }

            notification.Recipients.Add(new NotificationRecipient
            {
                NotificationId = notification.Id,
                UserId = userId,
            });
        }

        if (notification.Recipients.Count == 0)
        {
            logger.LogInformation(
                "SendToTeamAsync: all recipients suppressed notification for team {TeamId}", teamId);
            return;
        }

        await repo.AddAsync(notification, cancellationToken);
        InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId));

        logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to team '{TeamName}' ({Count} recipients)",
            source, title, team.Name, notification.Recipients.Count);
    }

    public async Task SendToRoleAsync(
        NotificationSource source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        string roleName,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();

        var roleUserIds = await recipientResolver.GetActiveUserIdsForRoleAsync(roleName, cancellationToken);
        if (roleUserIds.Count == 0)
        {
            logger.LogWarning("SendToRoleAsync: no active users found for role '{RoleName}'", roleName);
            return;
        }

        var category = source.ToMessageCategory();
        var inboxDisabled = await preferenceService.GetUsersWithInboxDisabledAsync(
            roleUserIds, category, cancellationToken);

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
            TargetGroupName = roleName,
            CreatedAt = now,
        };

        foreach (var userId in roleUserIds)
        {
            if (notificationClass == NotificationClass.Informational && inboxDisabled.Contains(userId))
            {
                continue;
            }

            notification.Recipients.Add(new NotificationRecipient
            {
                NotificationId = notification.Id,
                UserId = userId,
            });
        }

        if (notification.Recipients.Count == 0)
        {
            logger.LogInformation(
                "SendToRoleAsync: all recipients suppressed notification for role '{RoleName}'", roleName);
            return;
        }

        await repo.AddAsync(notification, cancellationToken);
        InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId));

        logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to role '{RoleName}' ({Count} recipients)",
            source, title, roleName, notification.Recipients.Count);
    }

    private void InvalidateBadgeCaches(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            cache.Remove(CacheKeys.NotificationBadgeCounts(userId));
        }
    }

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
        => repo.ReassignRecipientsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

    public void InvalidateBadgeCachesForUsers(IEnumerable<Guid> userIds) =>
        InvalidateBadgeCaches(userIds);
}
