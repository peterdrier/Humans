using Humans.Notifications.Domain;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Notifications.Data;

internal sealed class NotificationRepository(IDbContextFactory<NotificationsDbContext> factory) : INotificationRepository
{
    public async Task AddRangeAsync(IReadOnlyList<Notification> notifications, CancellationToken ct = default)
    {
        if (notifications.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Notifications.AddRange(notifications);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddAsync(Notification notification, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Notifications.Add(notification);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<NotificationMutationOutcome> ResolveAsync(
        Guid notificationId, Guid actorUserId, Instant now, NotificationClass expectedClass, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var notification = await ctx.Notifications
            .Include(n => n.Recipients)
            .FirstOrDefaultAsync(n => n.Id == notificationId, ct);

        if (notification is null)
            return new NotificationMutationOutcome(false, NotFound: true, Forbidden: false);

        if (!notification.Recipients.Any(r => r.UserId == actorUserId))
            return new NotificationMutationOutcome(false, NotFound: false, Forbidden: true);

        if (notification.Class != expectedClass)
            return new NotificationMutationOutcome(false, NotFound: false, Forbidden: true);

        if (notification.ResolvedAt is not null)
            return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false);

        await PersistResolutionAsync(ctx, [notification], now, actorUserId, ct);

        return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false);
    }

    public async Task<NotificationMutationOutcome> MarkReadAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var recipient = await ctx.NotificationRecipients
            .FirstOrDefaultAsync(nr => nr.NotificationId == notificationId && nr.UserId == userId, ct);

        if (recipient is null)
            return new NotificationMutationOutcome(false, NotFound: true, Forbidden: false);

        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = now;
            await ctx.SaveChangesAsync(ct);
        }

        return new NotificationMutationOutcome(true, NotFound: false, Forbidden: false);
    }

    public async Task MarkAllReadAsync(Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var unread = await ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId && nr.ReadAt == null)
            .ToListAsync(ct);

        if (unread.Count == 0) return;

        foreach (var nr in unread)
        {
            nr.ReadAt = now;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task BulkResolveAsync(
        IReadOnlyList<Guid> notificationIds, Guid userId, Instant now, NotificationClass expectedClass, CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var notifications = await ctx.Notifications
            .Where(n => notificationIds.Contains(n.Id) &&
                        n.Class == expectedClass &&
                        n.ResolvedAt == null &&
                        n.Recipients.Any(r => r.UserId == userId))
            .ToListAsync(ct);

        await PersistResolutionAsync(ctx, notifications, now, userId, ct);
    }

    public async Task<ClickThroughOutcome> ClickThroughAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var recipient = await ctx.NotificationRecipients
            .Include(nr => nr.Notification)
            .FirstOrDefaultAsync(nr => nr.NotificationId == notificationId && nr.UserId == userId, ct);

        if (recipient is null)
            return new ClickThroughOutcome(ActionUrl: null, NotFound: true);

        if (recipient.ReadAt is null)
        {
            recipient.ReadAt = now;
            await ctx.SaveChangesAsync(ct);
        }

        return new ClickThroughOutcome(recipient.Notification.ActionUrl, NotFound: false);
    }

    public async Task ResolveBySourceAsync(
        Guid userId, NotificationSource source, Instant now, CancellationToken ct = default, string? sourceKey = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var notifications = await ctx.Notifications
            .Where(n => n.Source == source
                        && (sourceKey == null || n.SourceKey == sourceKey)
                        && n.ResolvedAt == null
                        && n.Recipients.Any(r => r.UserId == userId))
            .ToListAsync(ct);

        await PersistResolutionAsync(ctx, notifications, now, userId, ct);
    }

    public async Task ResolveBySourceKeyAsync(
        NotificationSource source, string sourceKey, Instant now,
        Guid? resolvedByUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var notifications = await ctx.Notifications
            .Where(n => n.Source == source
                        && n.SourceKey == sourceKey
                        && n.ResolvedAt == null)
            .ToListAsync(ct);

        await PersistResolutionAsync(ctx, notifications, now, resolvedByUserId, ct);
    }

    private static async Task PersistResolutionAsync(
        NotificationsDbContext ctx, IReadOnlyCollection<Notification> notifications,
        Instant now, Guid? resolvedByUserId, CancellationToken ct)
    {
        if (notifications.Count == 0) return;

        foreach (var notification in notifications)
        {
            notification.ResolvedAt = now;
            notification.ResolvedByUserId = resolvedByUserId;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteUnresolvedBySourcesAsync(
        IReadOnlyList<NotificationSource> sources, CancellationToken ct = default)
    {
        if (sources.Count == 0) return 0;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var toDelete = await ctx.Notifications
            .Include(n => n.Recipients)
            .Where(n => n.ResolvedAt == null && sources.Contains(n.Source))
            .ToListAsync(ct);

        if (toDelete.Count == 0) return 0;

        ctx.Notifications.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    public async Task<int> DeleteResolvedOlderThanAsync(Instant resolvedCutoff, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var toDelete = await ctx.Notifications
            .Include(n => n.Recipients)
            .Where(n => n.ResolvedAt == null &&
                        n.Class == NotificationClass.Informational &&
                        n.CreatedAt < createdCutoff)
            .ToListAsync(ct);

        if (toDelete.Count == 0) return 0;

        ctx.Notifications.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    public async Task<IReadOnlyList<NotificationRecipient>> GetInboxAsync(
        Guid userId,
        string? search,
        NotificationInboxFilter filter,
        NotificationInboxTab tab,
        Instant resolvedCutoff,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var query = ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId)
            .Include(nr => nr.Notification)
            .AsNoTracking();

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
                    // ConsentReviewNeeded and ApplicationSubmitted: retired sources, kept so existing rows still filter; nothing emits them.
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
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationRecipient>> GetPopupAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId && nr.Notification.ResolvedAt == null)
            .Include(nr => nr.Notification)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<(int Actionable, int Informational)> GetUnreadBadgeCountsAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.NotificationRecipients
            .AsNoTracking()
            .Include(nr => nr.Notification)
            .Where(nr => nr.UserId == userId)
            // arch:db-sort-ok GDPR export: sole caller is IUserDataContributor.ContributeForUserAsync; no controller-layer caller can re-sort the exported slice
            .OrderByDescending(nr => nr.Notification.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<int> EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var own = await ctx.NotificationRecipients
            .Where(nr => nr.UserId == userId)
            .ToListAsync(ct);
        ctx.NotificationRecipients.RemoveRange(own);

        // A notification addressed only to this human carries their title/body — take it with them.
        var notificationIds = own.Select(nr => nr.NotificationId).Distinct().ToList();
        var stillAddressed = await ctx.NotificationRecipients
            .Where(nr => notificationIds.Contains(nr.NotificationId) && nr.UserId != userId)
            .Select(nr => nr.NotificationId)
            .Distinct()
            .ToListAsync(ct);
        var orphanIds = notificationIds.Except(stillAddressed).ToList();
        var orphans = await ctx.Notifications
            .Where(n => orphanIds.Contains(n.Id))
            .ToListAsync(ct);
        ctx.Notifications.RemoveRange(orphans);

        var resolved = await ctx.Notifications
            .Where(n => n.ResolvedByUserId == userId)
            .ToListAsync(ct);
        foreach (var n in resolved.Where(n => !orphanIds.Contains(n.Id)))
        {
            n.ResolvedByUserId = null;
        }

        return await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignRecipientsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var sourceRows = await ctx.NotificationRecipients
            .Where(nr => nr.UserId == sourceUserId)
            .ToListAsync(ct);

        var targetNotificationIds = await ctx.NotificationRecipients
            .Where(nr => nr.UserId == targetUserId)
            .Select(nr => nr.NotificationId)
            .ToListAsync(ct);
        var targetNotificationIdSet = new HashSet<Guid>(targetNotificationIds);

        foreach (var src in sourceRows)
        {
            // UserId is init-only (composite key), so re-FK is remove + add, keeping ReadAt. When the target
            // already has a row, the source's is just dropped.
            ctx.NotificationRecipients.Remove(src);

            if (targetNotificationIdSet.Contains(src.NotificationId))
            {
                continue;
            }

            ctx.NotificationRecipients.Add(new NotificationRecipient
            {
                NotificationId = src.NotificationId,
                UserId = targetUserId,
                ReadAt = src.ReadAt,
            });
        }

        // So "Resolved by" keeps pointing at a live user rather than a tombstone.
        var resolvedBySource = await ctx.Notifications
            .Where(n => n.ResolvedByUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var n in resolvedBySource)
        {
            n.ResolvedByUserId = targetUserId;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
