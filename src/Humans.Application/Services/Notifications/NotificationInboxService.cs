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
/// </summary>
/// <remarks>
/// <para>
/// Goes through <see cref="INotificationRepository"/> for all data access —
/// this type never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </para>
/// <para>
/// Recipient and resolver display names are stitched in memory by calling
/// <c>IUserService.GetByIdsAsync</c>, replacing the prior cross-domain
/// <c>.Include(nr =&gt; nr.Notification.Recipients).ThenInclude(r =&gt; r.User)</c>
/// chain (design-rules §6).
/// </para>
/// </remarks>
public sealed class NotificationInboxService : INotificationInboxService, IUserDataContributor
{
    private readonly INotificationRepository _repo;
    private readonly IUserService _userService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;

    public NotificationInboxService(
        INotificationRepository repo,
        IUserService userService,
        IClock clock,
        IMemoryCache cache)
    {
        _repo = repo;
        _userService = userService;
        _clock = clock;
        _cache = cache;
    }

    public async Task<NotificationInboxResult> GetInboxAsync(
        Guid userId, string? search, string filter, string tab,
        CancellationToken ct = default)
    {
        var (parsedFilter, effectiveTab) = ParseFilterAndTab(filter, tab);
        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var recipients = await _repo.GetInboxAsync(
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
        var recipients = await _repo.GetPopupAsync(userId, ct);

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
        var outcome = await _repo.ResolveAsync(notificationId, userId, _clock.GetCurrentInstant(), ct);
        if (outcome.Success)
            InvalidateBadgeCaches(outcome.AffectedUserIds);
        return ToActionResult(outcome);
    }

    public async Task<NotificationActionResult> DismissAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var outcome = await _repo.DismissAsync(notificationId, userId, _clock.GetCurrentInstant(), ct);
        if (outcome.Success)
            InvalidateBadgeCaches(outcome.AffectedUserIds);
        return ToActionResult(outcome);
    }

    public async Task<NotificationActionResult> MarkReadAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var outcome = await _repo.MarkReadAsync(notificationId, userId, _clock.GetCurrentInstant(), ct);
        if (outcome.Success)
            InvalidateBadgeCaches(outcome.AffectedUserIds);
        return ToActionResult(outcome);
    }

    public async Task MarkAllReadAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var updated = await _repo.MarkAllReadAsync(userId, _clock.GetCurrentInstant(), ct);
        if (updated > 0)
            InvalidateBadgeCaches([userId]);
    }

    public async Task BulkResolveAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return;
        var affected = await _repo.BulkResolveAsync(notificationIds, userId, _clock.GetCurrentInstant(), ct);
        if (affected.Count > 0)
            InvalidateBadgeCaches(affected);
    }

    public async Task BulkDismissAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return;
        var affected = await _repo.BulkDismissAsync(notificationIds, userId, _clock.GetCurrentInstant(), ct);
        if (affected.Count > 0)
            InvalidateBadgeCaches(affected);
    }

    public async Task<string?> ClickThroughAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default)
    {
        var outcome = await _repo.ClickThroughAsync(notificationId, userId, _clock.GetCurrentInstant(), ct);
        if (outcome.NotFound) return null;
        if (outcome.MarkedRead)
            InvalidateBadgeCaches([userId]);
        return outcome.ActionUrl;
    }

    public async Task ResolveBySourceAsync(
        Guid userId, NotificationSource source,
        CancellationToken ct = default)
    {
        var updated = await _repo.ResolveBySourceAsync(userId, source, _clock.GetCurrentInstant(), ct);
        if (updated)
            InvalidateBadgeCaches([userId]);
    }

    public Task<(int Actionable, int Informational)> GetUnreadBadgeCountsAsync(
        Guid userId, CancellationToken ct = default) =>
        _repo.GetUnreadBadgeCountsAsync(userId, ct);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var recipients = await _repo.GetAllForUserContributorAsync(userId, ct);

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

    // ==========================================================================
    // Helpers
    // ==========================================================================

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
        // never unread; flip tab to All in that case (matches the pre-§15 behavior).
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

        var users = await _userService.GetByIdsAsync(userIds, ct);
        return users.ToDictionary(kv => kv.Key, kv => kv.Value.DisplayName ?? string.Empty);
    }

    private static NotificationRowDto MapToRow(
        NotificationRecipient nr,
        IReadOnlyDictionary<Guid, string> displayNames)
    {
        var n = nr.Notification;
        var allRecipients = n.Recipients?.ToList() ?? [];

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
            ResolvedByName = resolvedByName,
            RecipientInitials = allRecipients
                .Take(3)
                .Select(r => GetInitials(displayNames.TryGetValue(r.UserId, out var dn) ? dn : null))
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

    private static NotificationActionResult ToActionResult(NotificationMutationOutcome outcome) =>
        new(outcome.Success, NotFound: outcome.NotFound, Forbidden: outcome.Forbidden);

    private void InvalidateBadgeCaches(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            _cache.Remove(CacheKeys.NotificationBadgeCounts(userId));
        }
    }
}
