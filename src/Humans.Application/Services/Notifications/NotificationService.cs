using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;

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
    private readonly INotificationRepository _repo;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        INotificationRepository repo,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        ICommunicationPreferenceService preferenceService,
        IClock clock,
        IMemoryCache cache,
        ILogger<NotificationService> logger)
    {
        _repo = repo;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _preferenceService = preferenceService;
        _clock = clock;
        _cache = cache;
        _logger = logger;
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
        CancellationToken cancellationToken = default)
    {
        if (recipientUserIds.Count == 0)
        {
            _logger.LogWarning("SendAsync called with empty recipient list for source {Source}, title '{Title}'",
                source, title);
            return;
        }

        var now = _clock.GetCurrentInstant();
        var category = source.ToMessageCategory();

        // Load inbox-disabled users for the category via the preference service.
        var inboxDisabled = await _preferenceService.GetUsersWithInboxDisabledAsync(
            recipientUserIds, category, cancellationToken);

        var notifications = new List<Notification>(recipientUserIds.Count);
        foreach (var userId in recipientUserIds)
        {
            // Check InboxEnabled preference — informational can be suppressed.
            if (notificationClass == NotificationClass.Informational && inboxDisabled.Contains(userId))
            {
                _logger.LogDebug(
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
            _logger.LogInformation(
                "SendAsync: all {Count} recipient(s) suppressed notification for source {Source}",
                recipientUserIds.Count, source);
            return;
        }

        await _repo.AddRangeAsync(notifications, cancellationToken);
        InvalidateBadgeCaches(notifications.Select(n => n.Recipients.Single().UserId));

        _logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to {Count} individual recipient(s)",
            source, title, notifications.Count);
    }

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
        var team = await _teamService.GetTeamByIdAsync(teamId, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("SendToTeamAsync: team {TeamId} not found", teamId);
            return;
        }

        var members = await _teamService.GetTeamMembersAsync(teamId, cancellationToken);
        var memberUserIds = members.Select(m => m.UserId).ToList();
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

        var roleUserIds = await _roleAssignmentService.GetActiveUserIdsForRoleAsync(roleName, cancellationToken);
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
