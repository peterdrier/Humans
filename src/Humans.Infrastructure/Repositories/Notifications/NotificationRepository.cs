using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Notifications;

/// <summary>
/// EF-backed implementation of <see cref="INotificationRepository"/>. The
/// only non-test file that touches <see cref="HumansDbContext.Notifications"/>
/// and <see cref="HumansDbContext.NotificationRecipients"/> after the
/// Notifications §15 migration lands. Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
/// <remarks>
/// Read methods deliberately do not <c>.Include</c> cross-domain navigations
/// (recipient <see cref="NotificationRecipient.User"/>,
/// resolver <see cref="Notification.ResolvedByUser"/>). Callers resolve
/// display names through <c>IUserService</c> and stitch results in memory,
/// preserving table ownership (§2c) and eliminating cross-domain joins (§6).
/// </remarks>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public NotificationRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Writes — notifications
    // ==========================================================================

    public async Task AddRangeAsync(IReadOnlyList<Notification> notifications, CancellationToken ct = default)
    {
        if (notifications.Count == 0) return;
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Notifications.AddRange(notifications);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddAsync(Notification notification, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Notifications.Add(notification);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<NotificationMutationOutcome> ResolveAsync(
        Guid notificationId, Guid actorUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var notification = await ctx.Notifications
            .Include(n => n.Recipients)
            .FirstOrDefaultAsync(n => n.Id == notificationId, ct);

        if (notification is null)
            return new NotificationMutationOutcome(false, NotFound: true, Forbidden: false, []);

        if (!notification.Recipients.Any(r => r.UserId == actorUserId))
            return new NotificationMutationOutcome(false, NotFound: false, Forbidden: true, []);

        var recipientIds = notification.Recipients.Select(r => r.UserId).ToList();

        if (notification.ResolvedAt is not null)
            return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false, recipientIds);

        notification.ResolvedAt = now;
        notification.ResolvedByUserId = actorUserId;
        await ctx.SaveChangesAsync(ct);

        return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false, recipientIds);
    }

    public async Task<NotificationMutationOutcome> DismissAsync(
        Guid notificationId, Guid actorUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var notification = await ctx.Notifications
            .Include(n => n.Recipients)
            .FirstOrDefaultAsync(n => n.Id == notificationId, ct);

        if (notification is null)
            return new NotificationMutationOutcome(false, NotFound: true, Forbidden: false, []);

        if (!notification.Recipients.Any(r => r.UserId == actorUserId))
            return new NotificationMutationOutcome(false, NotFound: false, Forbidden: true, []);

        if (notification.Class == NotificationClass.Actionable)
            return new NotificationMutationOutcome(false, NotFound: false, Forbidden: true, []);

        var recipientIds = notification.Recipients.Select(r => r.UserId).ToList();

        if (notification.ResolvedAt is not null)
            return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false, recipientIds);

        notification.ResolvedAt = now;
        notification.ResolvedByUserId = actorUserId;
        await ctx.SaveChangesAsync(ct);

        return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false, recipientIds);
    }

    public async Task<NotificationMutationOutcome> MarkReadAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var recipient = await ctx.NotificationRecipients
            .FirstOrDefaultAsync(nr => nr.NotificationId == notificationId && nr.UserId == userId, ct);

        if (recipient is null)
            return new NotificationMutationOutcome(false, NotFound: true, Forbidden: false, []);

        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = now;
            await ctx.SaveChangesAsync(ct);
        }

        return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false, [userId]);
    }

    public async Task<int> MarkAllReadAsync(Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var unread = await ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId && nr.ReadAt == null)
            .ToListAsync(ct);

        if (unread.Count == 0) return 0;

        foreach (var nr in unread)
        {
            nr.ReadAt = now;
        }

        await ctx.SaveChangesAsync(ct);
        return unread.Count;
    }

    public async Task<IReadOnlyList<Guid>> BulkResolveAsync(
        IReadOnlyList<Guid> notificationIds, Guid userId, Instant now, CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var notifications = await ctx.Notifications
            .Include(n => n.Recipients)
            .Where(n => notificationIds.Contains(n.Id) &&
                        n.Class == NotificationClass.Actionable &&
                        n.ResolvedAt == null)
            .ToListAsync(ct);

        var affected = new HashSet<Guid>();

        foreach (var notification in notifications)
        {
            if (notification.Recipients.Any(r => r.UserId == userId))
            {
                notification.ResolvedAt = now;
                notification.ResolvedByUserId = userId;
                foreach (var r in notification.Recipients)
                    affected.Add(r.UserId);
            }
        }

        if (affected.Count > 0)
            await ctx.SaveChangesAsync(ct);

        return [.. affected];
    }

    public async Task<IReadOnlyList<Guid>> BulkDismissAsync(
        IReadOnlyList<Guid> notificationIds, Guid userId, Instant now, CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var notifications = await ctx.Notifications
            .Include(n => n.Recipients)
            .Where(n => notificationIds.Contains(n.Id) &&
                        n.Class == NotificationClass.Informational &&
                        n.ResolvedAt == null)
            .ToListAsync(ct);

        var affected = new HashSet<Guid>();

        foreach (var notification in notifications)
        {
            if (notification.Recipients.Any(r => r.UserId == userId))
            {
                notification.ResolvedAt = now;
                notification.ResolvedByUserId = userId;
                foreach (var r in notification.Recipients)
                    affected.Add(r.UserId);
            }
        }

        if (affected.Count > 0)
            await ctx.SaveChangesAsync(ct);

        return [.. affected];
    }

    public async Task<ClickThroughOutcome> ClickThroughAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var recipient = await ctx.NotificationRecipients
            .Include(nr => nr.Notification)
            .FirstOrDefaultAsync(nr => nr.NotificationId == notificationId && nr.UserId == userId, ct);

        if (recipient is null)
            return new ClickThroughOutcome(ActionUrl: null, NotFound: true, MarkedRead: false);

        var markedRead = false;
        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = now;
            await ctx.SaveChangesAsync(ct);
            markedRead = true;
        }

        return new ClickThroughOutcome(recipient.Notification.ActionUrl, NotFound: false, MarkedRead: markedRead);
    }

    public async Task<bool> ResolveBySourceAsync(
        Guid userId, NotificationSource source, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var notifications = await ctx.Notifications
            .Where(n => n.Source == source
                        && n.ResolvedAt == null
                        && n.Recipients.Any(r => r.UserId == userId))
            .ToListAsync(ct);

        if (notifications.Count == 0) return false;

        foreach (var notification in notifications)
        {
            notification.ResolvedAt = now;
            notification.ResolvedByUserId = userId;
        }

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteResolvedOlderThanAsync(Instant resolvedCutoff, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var toDelete = await ctx.Notifications
            .Where(n => n.ResolvedAt != null && n.ResolvedAt < resolvedCutoff)
            .ToListAsync(ct);

        if (toDelete.Count == 0) return 0;

        ctx.Notifications.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    public async Task<int> DeleteUnresolvedInformationalOlderThanAsync(
        Instant createdCutoff, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var toDelete = await ctx.Notifications
            .Where(n => n.ResolvedAt == null &&
                        n.Class == NotificationClass.Informational &&
                        n.CreatedAt < createdCutoff)
            .ToListAsync(ct);

        if (toDelete.Count == 0) return 0;

        ctx.Notifications.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    // ==========================================================================
    // Reads — inbox / popup
    // ==========================================================================

    public async Task<IReadOnlyList<NotificationRecipient>> GetInboxAsync(
        Guid userId,
        string? search,
        NotificationInboxFilter filter,
        NotificationInboxTab tab,
        Instant resolvedCutoff,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.Recipients)
            .AsNoTrackingWithIdentityResolution();

        if (tab == NotificationInboxTab.Unread)
        {
            query = query.Where(nr => nr.Notification.ResolvedAt == null && nr.ReadAt == null);
        }
        else
        {
            query = query.Where(nr =>
                nr.Notification.ResolvedAt == null ||
                nr.Notification.ResolvedAt > resolvedCutoff);
        }

        switch (filter)
        {
            case NotificationInboxFilter.Action:
                query = query.Where(nr => nr.Notification.Class == NotificationClass.Actionable);
                break;
            case NotificationInboxFilter.Shifts:
                query = query.Where(nr =>
                    nr.Notification.Source == NotificationSource.ShiftCoverageGap ||
                    nr.Notification.Source == NotificationSource.ShiftSignupChange);
                break;
            case NotificationInboxFilter.Approvals:
                query = query.Where(nr =>
                    nr.Notification.Source == NotificationSource.ConsentReviewNeeded ||
                    nr.Notification.Source == NotificationSource.ApplicationSubmitted ||
                    nr.Notification.Source == NotificationSource.ApplicationApproved ||
                    nr.Notification.Source == NotificationSource.ApplicationRejected ||
                    nr.Notification.Source == NotificationSource.VolunteerApproved ||
                    nr.Notification.Source == NotificationSource.TeamJoinRequestSubmitted ||
                    nr.Notification.Source == NotificationSource.TeamJoinRequestDecided);
                break;
            case NotificationInboxFilter.Resolved:
                query = query.Where(nr => nr.Notification.ResolvedAt != null);
                break;
            case NotificationInboxFilter.All:
            default:
                break;
        }

        if (!string.IsNullOrWhiteSpace(search) && search.Trim().Length >= 2)
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(nr =>
                EF.Functions.ILike(nr.Notification.Title, term) ||
                (nr.Notification.Body != null && EF.Functions.ILike(nr.Notification.Body, term)));
        }

        return await query
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationRecipient>> GetPopupAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId && nr.Notification.ResolvedAt == null)
            .Include(nr => nr.Notification)
                .ThenInclude(n => n.Recipients)
            .AsNoTrackingWithIdentityResolution()
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<(int Actionable, int Informational)> GetUnreadBadgeCountsAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var counts = await ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId && nr.ReadAt == null && nr.Notification.ResolvedAt == null)
            .GroupBy(nr => nr.Notification.Class)
            .Select(g => new { Class = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var actionable = counts.FirstOrDefault(c => c.Class == NotificationClass.Actionable)?.Count ?? 0;
        var informational = counts.FirstOrDefault(c => c.Class == NotificationClass.Informational)?.Count ?? 0;
        return (actionable, informational);
    }

    public async Task<IReadOnlyList<NotificationRecipient>> GetAllForUserContributorAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.NotificationRecipients
            .AsNoTracking()
            .Include(nr => nr.Notification)
            .Where(nr => nr.UserId == userId)
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetRecipientUserIdsAsync(
        IReadOnlyCollection<Guid> notificationIds, CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.NotificationRecipients
            .AsNoTracking()
            .Where(nr => notificationIds.Contains(nr.NotificationId))
            .Select(nr => nr.UserId)
            .Distinct()
            .ToListAsync(ct);
    }
}
