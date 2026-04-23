using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Application-layer implementation of <see cref="INotificationService"/>.
/// Dispatches in-app notifications, materializes recipients, checks
/// preferences, and delegates persistence to
/// <see cref="INotificationRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Goes through <see cref="INotificationRepository"/> for all data access —
/// this type never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </para>
/// <para>
/// No caching decorator (§15 Option A): in-app notification dispatch is
/// fire-and-forget — reads go through <see cref="NotificationInboxService"/>,
/// whose nav-badge counts are cached in the view component layer via a
/// short-TTL <see cref="IMemoryCache"/> entry keyed by user. This service
/// invalidates those per-user cache keys after every successful send.
/// </para>
/// </remarks>
public sealed class NotificationService : INotificationService
{
    private readonly INotificationEmitter _emitter;
    private readonly INotificationRepository _repo;
    private readonly INotificationRecipientResolver _recipientResolver;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        INotificationEmitter emitter,
        INotificationRepository repo,
        INotificationRecipientResolver recipientResolver,
        ICommunicationPreferenceService preferenceService,
        IClock clock,
        IMemoryCache cache,
        ILogger<NotificationService> logger)
    {
        _emitter = emitter;
        _repo = repo;
        _recipientResolver = recipientResolver;
        _preferenceService = preferenceService;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

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
        _emitter.SendAsync(
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
        var team = await _recipientResolver.GetTeamNotificationInfoAsync(teamId, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("SendToTeamAsync: team {TeamId} not found", teamId);
            return;
        }

        var memberUserIds = team.MemberUserIds;
        if (memberUserIds.Count == 0)
        {
            _logger.LogWarning("SendToTeamAsync: team {TeamId} has no members", teamId);
            return;
        }

        var now = _clock.GetCurrentInstant();
        var category = source.ToMessageCategory();

        var inboxDisabled = await _preferenceService.GetUsersWithInboxDisabledAsync(
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
            _logger.LogInformation(
                "SendToTeamAsync: all recipients suppressed notification for team {TeamId}", teamId);
            return;
        }

        await _repo.AddAsync(notification, cancellationToken);
        InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId));

        _logger.LogInformation(
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
        var now = _clock.GetCurrentInstant();

        var roleUserIds = await _recipientResolver.GetActiveUserIdsForRoleAsync(roleName, cancellationToken);
        if (roleUserIds.Count == 0)
        {
            _logger.LogWarning("SendToRoleAsync: no active users found for role '{RoleName}'", roleName);
            return;
        }

        var category = source.ToMessageCategory();
        var inboxDisabled = await _preferenceService.GetUsersWithInboxDisabledAsync(
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
            _logger.LogInformation(
                "SendToRoleAsync: all recipients suppressed notification for role '{RoleName}'", roleName);
            return;
        }

        await _repo.AddAsync(notification, cancellationToken);
        InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId));

        _logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to role '{RoleName}' ({Count} recipients)",
            source, title, roleName, notification.Recipients.Count);
    }

    private void InvalidateBadgeCaches(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            _cache.Remove(CacheKeys.NotificationBadgeCounts(userId));
        }
    }
}
