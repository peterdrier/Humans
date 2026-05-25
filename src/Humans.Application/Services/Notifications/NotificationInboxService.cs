using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Application-layer implementation of <see cref="INotificationInboxService"/>.
/// Builds read models for the inbox and popup, handles resolve/dismiss/
/// mark-read actions, and invalidates nav-badge cache entries after writes.
/// Cross-domain display names are stitched via <c>IUserService.GetByIdsAsync</c>
/// (design-rules §6).
/// </summary>
public sealed class NotificationInboxService(
    INotificationRepository repo,
    IUserServiceRead userService,
    IClock clock,
    IMemoryCache cache) : INotificationInboxService, IUserDataContributor
{
    public async Task<NotificationInboxResult> GetInboxAsync(
        Guid userId, string? search, string filter, string tab,
        CancellationToken ct = default)
    {
        var (parsedFilter, effectiveTab) = ParseFilterAndTab(filter, tab);
        var now = clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var recipients = await repo.GetInboxAsync(
            userId, search, parsedFilter, effectiveTab, cutoff, ct);

        var displayNames = await LoadDisplayNamesAsync(recipients, ct);

        var needsAttention = new List<NotificationRowDto>();
        var informational = new List<NotificationRowDto>();
        var resolved = new List<NotificationRowDto>();

        foreach (var nr in recipients)
        {
            var row = MapToRow(nr, displayNames);
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
        var recipients = await repo.GetPopupAsync(userId, ct);

        var displayNames = await LoadDisplayNamesAsync(recipients, ct);

        var actionable = new List<NotificationRowDto>();
        var informational = new List<NotificationRowDto>();

        foreach (var nr in recipients)
        {
            var row = MapToRow(nr, displayNames);
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
        var outcome = await repo.ResolveAsync(notificationId, userId, clock.GetCurrentInstant(), ct);
        if (outcome.Success)
            InvalidateBadgeCaches(outcome.AffectedUserIds);
        return ToActionResult(outcome);
    }

    public async Task<NotificationActionResult> DismissAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var outcome = await repo.DismissAsync(notificationId, userId, clock.GetCurrentInstant(), ct);
        if (outcome.Success)
            InvalidateBadgeCaches(outcome.AffectedUserIds);
        return ToActionResult(outcome);
    }

    public async Task<NotificationActionResult> MarkReadAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var outcome = await repo.MarkReadAsync(notificationId, userId, clock.GetCurrentInstant(), ct);
        if (outcome.Success)
            InvalidateBadgeCaches(outcome.AffectedUserIds);
        return ToActionResult(outcome);
    }

    public async Task MarkAllReadAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var updated = await repo.MarkAllReadAsync(userId, clock.GetCurrentInstant(), ct);
        if (updated > 0)
            InvalidateBadgeCaches([userId]);
    }

    public async Task BulkResolveAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return;
        var affected = await repo.BulkResolveAsync(notificationIds, userId, clock.GetCurrentInstant(), ct);
        if (affected.Count > 0)
            InvalidateBadgeCaches(affected);
    }

    public async Task BulkDismissAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return;
        var affected = await repo.BulkDismissAsync(notificationIds, userId, clock.GetCurrentInstant(), ct);
        if (affected.Count > 0)
            InvalidateBadgeCaches(affected);
    }

    public async Task<string?> ClickThroughAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var outcome = await repo.ClickThroughAsync(notificationId, userId, clock.GetCurrentInstant(), ct);
        if (outcome.NotFound) return null;
        if (outcome.MarkedRead)
            InvalidateBadgeCaches([userId]);
        return outcome.ActionUrl;
    }

    public async Task ResolveBySourceAsync(
        Guid userId, NotificationSource source,
        CancellationToken ct = default)
    {
        var updated = await repo.ResolveBySourceAsync(userId, source, clock.GetCurrentInstant(), ct);
        if (updated)
            InvalidateBadgeCaches([userId]);
    }

    public Task<(int Actionable, int Informational)> GetUnreadBadgeCountsAsync(
        Guid userId, CancellationToken ct = default) =>
        repo.GetUnreadBadgeCountsAsync(userId, ct);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var recipients = await repo.GetAllForUserContributorAsync(userId, ct);

        var shaped = recipients.Select(nr => new
        {
            nr.Notification.Title,
            nr.Notification.Body,
            nr.Notification.ActionUrl,
            nr.Notification.Priority,
            nr.Notification.Source,
            CreatedAt = nr.Notification.CreatedAt.ToInvariantInstantString(),
            ReadAt = nr.ReadAt.ToInvariantInstantString(),
            ResolvedAt = nr.Notification.ResolvedAt.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.Notifications, shaped)];
    }

    private static (NotificationInboxFilter Filter, NotificationInboxTab Tab)
        ParseFilterAndTab(string filter, string tab)
    {
        var parsedFilter = filter?.ToLowerInvariant() switch
        {
            "action" => NotificationInboxFilter.Action,
            "shifts" => NotificationInboxFilter.Shifts,
            "approvals" => NotificationInboxFilter.Approvals,
            "resolved" => NotificationInboxFilter.Resolved,
            _ => NotificationInboxFilter.All,
        };

        // Resolved filter is incompatible with unread tab — resolved items are
        // never unread; flip tab to All in that case.
        var parsedTab = parsedFilter == NotificationInboxFilter.Resolved
            ? NotificationInboxTab.All
            : tab?.ToLowerInvariant() switch
            {
                "unread" => NotificationInboxTab.Unread,
                _ => NotificationInboxTab.All,
            };

        return (parsedFilter, parsedTab);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadDisplayNamesAsync(
        IReadOnlyList<NotificationRecipient> recipients, CancellationToken ct)
    {
        var userIds = new HashSet<Guid>();
        foreach (var nr in recipients)
        {
            if (nr.Notification.ResolvedByUserId is { } resolverId)
                userIds.Add(resolverId);
            foreach (var r in nr.Notification.Recipients)
                userIds.Add(r.UserId);
        }

        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        var users = await userService.GetUserInfosAsync(userIds, ct);
        return users.ToDictionary(kv => kv.Key, kv => kv.Value.BurnerName);
    }

    private static NotificationRowDto MapToRow(
        NotificationRecipient nr,
        IReadOnlyDictionary<Guid, string> displayNames)
    {
        var n = nr.Notification;

        string? resolvedByName = null;
        if (n.ResolvedByUserId is { } resolverId &&
            displayNames.TryGetValue(resolverId, out var name))
        {
            resolvedByName = name;
        }

        return new NotificationRowDto
        {
            Id = n.Id,
            Title = n.Title,
            ActionUrl = n.ActionUrl,
            ActionLabel = n.ActionLabel,
            Priority = n.Priority,
            Source = n.Source,
            Class = n.Class,
            CreatedAt = n.CreatedAt.ToDateTimeUtc(),
            IsRead = nr.ReadAt is not null,
            IsResolved = n.ResolvedAt is not null,
            ResolvedAt = n.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = resolvedByName,
        };
    }

    private static NotificationActionResult ToActionResult(NotificationMutationOutcome outcome) =>
        new(outcome.Success, NotFound: outcome.NotFound, Forbidden: outcome.Forbidden);

    private void InvalidateBadgeCaches(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            cache.Remove(CacheKeys.NotificationBadgeCounts(userId));
        }
    }
}
