using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for notification inbox read models and write operations.
/// </summary>
public class NotificationInboxService : INotificationInboxService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;

    public NotificationInboxService(
        HumansDbContext dbContext,
        IClock clock,
        IMemoryCache cache)
    {
        _dbContext = dbContext;
        _clock = clock;
        _cache = cache;
    }

    public async Task<NotificationInboxResult> GetInboxAsync(
        Guid userId, string? search, string filter, string tab,
        CancellationToken ct = default)
    {
        // Resolved filter is incompatible with unread tab — resolved items are never unread
        if (string.Equals(filter, "resolved", StringComparison.OrdinalIgnoreCase))
            tab = "all";

        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var query = _dbContext.NotificationRecipients
            .Where(nr => nr.UserId == userId)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.ResolvedByUser)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.Recipients)
                    .ThenInclude(r => r.User)
            .AsNoTrackingWithIdentityResolution();

        // Tab filter
        if (string.Equals(tab, "unread", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr => nr.Notification.ResolvedAt == null && nr.ReadAt == null);
        }
        else
        {
            // All: unresolved + resolved within last 7 days
            query = query.Where(nr =>
                nr.Notification.ResolvedAt == null ||
                nr.Notification.ResolvedAt > cutoff);
        }

        // Filter pills
        if (string.Equals(filter, "action", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr => nr.Notification.Class == NotificationClass.Actionable);
        }
        else if (string.Equals(filter, "shifts", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr =>
                nr.Notification.Source == NotificationSource.ShiftCoverageGap ||
                nr.Notification.Source == NotificationSource.ShiftSignupChange);
        }
        else if (string.Equals(filter, "approvals", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr =>
                nr.Notification.Source == NotificationSource.ConsentReviewNeeded ||
                nr.Notification.Source == NotificationSource.ApplicationSubmitted ||
                nr.Notification.Source == NotificationSource.ApplicationApproved ||
                nr.Notification.Source == NotificationSource.ApplicationRejected ||
                nr.Notification.Source == NotificationSource.VolunteerApproved ||
                nr.Notification.Source == NotificationSource.TeamJoinRequestSubmitted ||
                nr.Notification.Source == NotificationSource.TeamJoinRequestDecided);
        }
        else if (string.Equals(filter, "resolved", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(nr => nr.Notification.ResolvedAt != null);
        }

        // Search
        if (!string.IsNullOrWhiteSpace(search) && search.Trim().Length >= 2)
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(nr =>
                EF.Functions.ILike(nr.Notification.Title, term) ||
                (nr.Notification.Body != null && EF.Functions.ILike(nr.Notification.Body, term)));
        }

        var recipients = await query
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync(ct);

        var needsAttention = new List<NotificationRowDto>();
        var informational = new List<NotificationRowDto>();
        var resolved = new List<NotificationRowDto>();

        foreach (var nr in recipients)
        {
            var row = MapToRow(nr);
            if (row.IsResolved)
                resolved.Add(row);
            else if (nr.Notification.Class == NotificationClass.Actionable)
                needsAttention.Add(row);
            else
                informational.Add(row);
        }

        var unreadCount = recipients.Count(nr =>
            nr.Notification.ResolvedAt == null && nr.ReadAt == null);

        return new NotificationInboxResult
        {
            NeedsAttention = needsAttention,
            Informational = informational,
            Resolved = resolved,
            UnreadCount = unreadCount,
        };
    }

    public async Task<NotificationPopupResult> GetPopupAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var recipients = await _dbContext.NotificationRecipients
            .Where(nr => nr.UserId == userId && nr.Notification.ResolvedAt == null)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.Recipients)
                    .ThenInclude(r => r.User)
            .AsNoTrackingWithIdentityResolution()
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync(ct);

        var actionable = new List<NotificationRowDto>();
        var informational = new List<NotificationRowDto>();

        foreach (var nr in recipients)
        {
            var row = MapToRow(nr);
            if (nr.Notification.Class == NotificationClass.Actionable)
                actionable.Add(row);
            else
                informational.Add(row);
        }

        return new NotificationPopupResult
        {
            Actionable = actionable,
            Informational = informational,
            ActionableCount = actionable.Count,
        };
    }

    public async Task<NotificationActionResult> ResolveAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var notification = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .FirstOrDefaultAsync(n => n.Id == notificationId, ct);

        if (notification is null)
            return new NotificationActionResult(false, NotFound: true);

        if (!notification.Recipients.Any(r => r.UserId == userId))
            return new NotificationActionResult(false, Forbidden: true);

        if (notification.ResolvedAt is null)
        {
            notification.ResolvedAt = _clock.GetCurrentInstant();
            notification.ResolvedByUserId = userId;
            await _dbContext.SaveChangesAsync(ct);
            InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId));
        }

        return new NotificationActionResult(true);
    }

    public async Task<NotificationActionResult> DismissAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var notification = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .FirstOrDefaultAsync(n => n.Id == notificationId, ct);

        if (notification is null)
            return new NotificationActionResult(false, NotFound: true);

        if (!notification.Recipients.Any(r => r.UserId == userId))
            return new NotificationActionResult(false, Forbidden: true);

        // Actionable notifications cannot be dismissed
        if (notification.Class == NotificationClass.Actionable)
            return new NotificationActionResult(false, Forbidden: true);

        if (notification.ResolvedAt is null)
        {
            notification.ResolvedAt = _clock.GetCurrentInstant();
            notification.ResolvedByUserId = userId;
            await _dbContext.SaveChangesAsync(ct);
            InvalidateBadgeCaches(notification.Recipients.Select(r => r.UserId));
        }

        return new NotificationActionResult(true);
    }

    public async Task<NotificationActionResult> MarkReadAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var recipient = await _dbContext.NotificationRecipients
            .FirstOrDefaultAsync(nr => nr.NotificationId == notificationId && nr.UserId == userId, ct);

        if (recipient is null)
            return new NotificationActionResult(false, NotFound: true);

        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = _clock.GetCurrentInstant();
            await _dbContext.SaveChangesAsync(ct);
            InvalidateBadgeCaches([userId]);
        }

        return new NotificationActionResult(true);
    }

    public async Task MarkAllReadAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var unread = await _dbContext.NotificationRecipients
            .Where(nr => nr.UserId == userId && nr.ReadAt == null)
            .ToListAsync(ct);

        foreach (var nr in unread)
        {
            nr.ReadAt = now;
        }

        if (unread.Count > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            InvalidateBadgeCaches([userId]);
        }
    }

    public async Task BulkResolveAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return;

        var now = _clock.GetCurrentInstant();

        var notifications = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .Where(n => notificationIds.Contains(n.Id) &&
                        n.Class == NotificationClass.Actionable &&
                        n.ResolvedAt == null)
            .ToListAsync(ct);

        foreach (var notification in notifications)
        {
            if (notification.Recipients.Any(r => r.UserId == userId))
            {
                notification.ResolvedAt = now;
                notification.ResolvedByUserId = userId;
            }
        }

        if (notifications.Count > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            var affectedUserIds = notifications.SelectMany(n => n.Recipients.Select(r => r.UserId)).Distinct();
            InvalidateBadgeCaches(affectedUserIds);
        }
    }

    public async Task BulkDismissAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return;

        var now = _clock.GetCurrentInstant();

        var notifications = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .Where(n => notificationIds.Contains(n.Id) &&
                        n.Class == NotificationClass.Informational &&
                        n.ResolvedAt == null)
            .ToListAsync(ct);

        foreach (var notification in notifications)
        {
            if (notification.Recipients.Any(r => r.UserId == userId))
            {
                notification.ResolvedAt = now;
                notification.ResolvedByUserId = userId;
            }
        }

        if (notifications.Count > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            var affectedUserIds = notifications.SelectMany(n => n.Recipients.Select(r => r.UserId)).Distinct();
            InvalidateBadgeCaches(affectedUserIds);
        }
    }

    public async Task<string?> ClickThroughAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var recipient = await _dbContext.NotificationRecipients
            .Include(nr => nr.Notification)
            .FirstOrDefaultAsync(nr => nr.NotificationId == notificationId && nr.UserId == userId, ct);

        if (recipient is null)
            return null;

        // Mark as read on click-through
        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = _clock.GetCurrentInstant();
            await _dbContext.SaveChangesAsync(ct);
            InvalidateBadgeCaches([userId]);
        }

        return recipient.Notification.ActionUrl;
    }

    public async Task ResolveBySourceAsync(
        Guid userId, NotificationSource source,
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var notifications = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .Where(n => n.Source == source
                && n.ResolvedAt == null
                && n.Recipients.Any(r => r.UserId == userId))
            .ToListAsync(ct);

        if (notifications.Count == 0)
            return;

        foreach (var notification in notifications)
        {
            notification.ResolvedAt = now;
            notification.ResolvedByUserId = userId;
        }

        await _dbContext.SaveChangesAsync(ct);
        InvalidateBadgeCaches([userId]);
    }

    private static NotificationRowDto MapToRow(NotificationRecipient nr)
    {
        var n = nr.Notification;
        var allRecipients = n.Recipients?.ToList() ?? [];

        return new NotificationRowDto
        {
            Id = n.Id,
            Title = n.Title,
            Body = n.Body,
            ActionUrl = n.ActionUrl,
            ActionLabel = n.ActionLabel,
            Priority = n.Priority,
            Source = n.Source,
            Class = n.Class,
            TargetGroupName = n.TargetGroupName,
            CreatedAt = n.CreatedAt.ToDateTimeUtc(),
            IsRead = nr.ReadAt is not null,
            IsResolved = n.ResolvedAt is not null,
            ResolvedAt = n.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = n.ResolvedByUser?.DisplayName,
            RecipientInitials = allRecipients
                .Take(3)
                .Select(r => GetInitials(r.User?.DisplayName))
                .ToList(),
            TotalRecipientCount = allRecipients.Count,
        };
    }

    private static string GetInitials(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "?";
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
            : parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
    }

    private void InvalidateBadgeCaches(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            _cache.Remove(CacheKeys.NotificationBadgeCounts(userId));
        }
    }
}
