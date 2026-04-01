using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Dispatches in-app notifications, materializes recipients, checks preferences,
/// and optionally queues email via the existing email outbox.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        HumansDbContext dbContext,
        IClock clock,
        IMemoryCache cache,
        ILogger<NotificationService> logger)
    {
        _dbContext = dbContext;
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

        // Load preferences for all recipients at once
        var preferences = await _dbContext.CommunicationPreferences
            .Where(cp => recipientUserIds.Contains(cp.UserId) && cp.Category == category)
            .ToListAsync(cancellationToken);

        var prefByUser = preferences.ToDictionary(p => p.UserId);

        // For individual target, create one notification per user
        foreach (var userId in recipientUserIds)
        {
            // Check InboxEnabled preference — informational can be suppressed
            if (notificationClass == NotificationClass.Informational)
            {
                if (prefByUser.TryGetValue(userId, out var pref) && !pref.InboxEnabled)
                {
                    _logger.LogDebug(
                        "Skipping informational notification for user {UserId} — InboxEnabled=false for {Category}",
                        userId, category);
                    continue;
                }
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

            _dbContext.Notifications.Add(notification);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateBadgeCaches(recipientUserIds);

        _logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to {Count} individual recipient(s)",
            source, title, recipientUserIds.Count);
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
        var team = await _dbContext.Teams
            .AsNoTracking()
            .Where(t => t.Id == teamId)
            .Select(t => new { t.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (team is null)
        {
            _logger.LogWarning("SendToTeamAsync: team {TeamId} not found", teamId);
            return;
        }

        var memberUserIds = await _dbContext.TeamMembers
            .Where(tm => tm.TeamId == teamId)
            .Select(tm => tm.UserId)
            .ToListAsync(cancellationToken);

        if (memberUserIds.Count == 0)
        {
            _logger.LogWarning("SendToTeamAsync: team {TeamId} has no members", teamId);
            return;
        }

        var now = _clock.GetCurrentInstant();
        var category = source.ToMessageCategory();

        // Load preferences for all team members
        var preferences = await _dbContext.CommunicationPreferences
            .Where(cp => memberUserIds.Contains(cp.UserId) && cp.Category == category)
            .ToListAsync(cancellationToken);

        var prefByUser = preferences.ToDictionary(p => p.UserId);

        // Group target: one shared notification
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
            // Check InboxEnabled preference — informational can be suppressed
            if (notificationClass == NotificationClass.Informational &&
                prefByUser.TryGetValue(userId, out var pref) && !pref.InboxEnabled)
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

        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId).ToList());

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

        // Find users with active role assignments
        var roleUserIds = await _dbContext.RoleAssignments
            .Where(ra => ra.RoleName == roleName &&
                         ra.ValidFrom <= now &&
                         (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (roleUserIds.Count == 0)
        {
            _logger.LogWarning("SendToRoleAsync: no active users found for role '{RoleName}'", roleName);
            return;
        }

        var category = source.ToMessageCategory();

        // Load preferences
        var preferences = await _dbContext.CommunicationPreferences
            .Where(cp => roleUserIds.Contains(cp.UserId) && cp.Category == category)
            .ToListAsync(cancellationToken);

        var prefByUser = preferences.ToDictionary(p => p.UserId);

        // Group target: one shared notification
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
            if (notificationClass == NotificationClass.Informational &&
                prefByUser.TryGetValue(userId, out var pref) && !pref.InboxEnabled)
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

        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId).ToList());

        _logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to role '{RoleName}' ({Count} recipients)",
            source, title, roleName, notification.Recipients.Count);
    }

    private void InvalidateBadgeCaches(IReadOnlyList<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            _cache.Remove(CacheKeys.NotificationBadgeCounts(userId));
        }
    }
}
