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
/// Minimal <see cref="INotificationEmitter"/> implementation that persists a
/// notification to a pre-resolved list of recipient user IDs. Has no
/// dependency on <see cref="INotificationRecipientResolver"/>, so
/// <see cref="ITeamService"/> and <see cref="IRoleAssignmentService"/> can
/// safely inject this interface without closing a DI cycle back through
/// <see cref="INotificationService"/>.
/// </summary>
/// <remarks>
/// <see cref="NotificationService"/> delegates its own
/// <see cref="INotificationEmitter.SendAsync"/> implementation to this type,
/// so there is only one copy of the dispatch logic.
/// </remarks>
public sealed class NotificationEmitter : INotificationEmitter
{
    private readonly INotificationRepository _repo;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NotificationEmitter> _logger;

    public NotificationEmitter(
        INotificationRepository repo,
        ICommunicationPreferenceService preferenceService,
        IClock clock,
        IMemoryCache cache,
        ILogger<NotificationEmitter> logger)
    {
        _repo = repo;
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

        var inboxDisabled = await _preferenceService.GetUsersWithInboxDisabledAsync(
            recipientUserIds, category, cancellationToken);

        var notifications = new List<Notification>(recipientUserIds.Count);
        foreach (var userId in recipientUserIds)
        {
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
        foreach (var n in notifications)
        {
            _cache.Remove(CacheKeys.NotificationBadgeCounts(n.Recipients.Single().UserId));
        }

        _logger.LogInformation(
            "Dispatched {Source} notification '{Title}' to {Count} individual recipient(s)",
            source, title, notifications.Count);
    }
}
