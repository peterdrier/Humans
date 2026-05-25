using System.Collections.Concurrent;
using System.Diagnostics;
using Humans.Application.DTOs.Events;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Events;

/// <summary>
/// T-03 — Singleton caching decorator for <see cref="IEventService"/>. Owns
/// four split projections — per-event <see cref="ApprovedEventView"/>,
/// flat <see cref="EventCategoryView"/> list, flat <see cref="EventVenueView"/>
/// list, and the <see cref="EventGuideSettingsView"/> singleton. Composes
/// <see cref="TrackedCache{TKey, TValue}"/> for diagnostics on
/// <c>/Admin/CacheStats</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pattern mirrors <c>CachingTeamService</c>: dict hits are served from the
/// in-memory snapshot; every write through this surface delegates to the
/// inner <see cref="IEventService"/> then invalidates the affected slice.
/// No <c>SaveChangesInterceptor</c> — every event_* write flows through
/// <see cref="IEventService"/> by design, enforced by the
/// <c>Only_EventRepository_Writes_Event_DbSets</c> architecture test.
/// </para>
/// <para>
/// The <c>GetAllEventsForDashboardAsync</c> moderator-only read passes
/// through to the inner service and goes straight to DB — moderation needs
/// a fresh pending count and the cache only holds approved events.
/// </para>
/// </remarks>
public sealed class CachingEventService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingEventService> logger) : IEventService, IEventViewInvalidator, IHostedService
{
    /// <summary>
    /// DI service key under which the undecorated inner <see cref="IEventService"/>
    /// is registered. The Singleton decorator resolves the Scoped inner via
    /// <see cref="IServiceScopeFactory"/> per-call.
    /// </summary>
    public const string InnerServiceKey = "event-inner";

    // warmOnStartup: false — the decorator owns cross-cutting warmup over all
    // four projections (events + categories + venues + settings) via its own
    // IHostedService surface; per-cache hosted-service kickoff would only see
    // the events dict.
    private readonly TrackedCache<Guid, ApprovedEventView> _eventCache = new(
        "Event.ApprovedEventView", warmOnStartup: false, logger);

    // Flat lookup tables — categories and venues are admin-managed lookups
    // (~10–30 rows each), so a simple immutable snapshot is the natural shape.
    // Updated atomically via field assignment under _loadLock.
    private volatile IReadOnlyList<EventCategoryView> _categories = [];
    private volatile IReadOnlyList<EventVenueView> _venues = [];
    private volatile EventGuideSettingsView? _settings;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _isLoaded;

    /// <summary>Diagnostics surface for <c>/Admin/CacheStats</c>.</summary>
    public ICacheStats EventCacheStats => _eventCache;

    // ==========================================================================
    // Settings — singleton projection
    // ==========================================================================

    public Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default) =>
        GetSettingsViewAsync(ct);

    public async Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default)
    {
        var view = await GetSettingsViewAsync(ct);
        if (view is null) return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        return view.IsSubmissionOpenAt(clock.GetCurrentInstant());
    }

    public Task<IReadOnlyList<BurnSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetEventSettingsOptionsAsync(ct));

    public Task<BurnSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default) =>
        WithInner(inner => inner.GetEventSettingsByIdAsync(id, ct));

    public async Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default)
    {
        await WithInner(inner => inner.SaveGuideSettingsAsync(
            existingId, eventSettingsId, submissionOpenAt, submissionCloseAt, guidePublishAt,
            maxPrintSlots, ct));
        await RefreshSettingsAsync(ct);
    }

    // ==========================================================================
    // Categories — flat list projection
    // ==========================================================================

    public async Task<IReadOnlyList<EventCategoryView>> GetActiveCategoriesAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _categories
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .ToList();
    }

    public async Task<IReadOnlyList<EventCategoryManageInfo>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        // Inner method includes .Events for management UI counts — the cache
        // doesn't carry that, so pass through.
        return await WithInner(inner => inner.GetAllCategoriesAsync(ct));
    }

    public async Task<EventCategoryView?> GetCategoryAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _categories.FirstOrDefault(c => c.Id == id);
    }

    public async Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _categories.Any(c =>
            string.Equals(c.Slug, slug, StringComparison.Ordinal)
            && (!excludeId.HasValue || c.Id != excludeId.Value));
    }

    public async Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return (_categories.Count == 0 ? 0 : _categories.Max(c => c.DisplayOrder)) + 1;
    }

    public async Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default)
    {
        await WithInner(inner => inner.CreateCategoryAsync(category, ct));
        await RefreshCategoriesAsync(ct);
    }

    public async Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default)
    {
        await WithInner(inner => inner.UpdateCategoryAsync(category, ct));
        await RefreshCategoriesAsync(ct);
        // A category rename/sensitive-flip changes flattened fields on every
        // approved event projection — refresh the event cache too.
        await RefreshAllEventsAsync(ct);
    }

    public async Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.DeleteCategoryAsync(id, ct));
        if (result.deleted)
            await RefreshCategoriesAsync(ct);
        return result;
    }

    public async Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default)
    {
        await WithInner(inner => inner.MoveCategoryAsync(id, direction, ct));
        await RefreshCategoriesAsync(ct);
    }

    // ==========================================================================
    // Venues — flat list projection
    // ==========================================================================

    public async Task<IReadOnlyList<EventVenueView>> GetActiveVenuesAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _venues
            .Where(v => v.IsActive)
            .OrderBy(v => v.DisplayOrder)
            .ToList();
    }

    public async Task<IReadOnlyList<EventVenueManageInfo>> GetAllVenuesAsync(CancellationToken ct = default)
    {
        // Inner method includes .Events for management UI counts — pass through.
        return await WithInner(inner => inner.GetAllVenuesAsync(ct));
    }

    public async Task<EventVenueView?> GetVenueAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _venues.FirstOrDefault(v => v.Id == id);
    }

    public async Task<int> GetNextVenueOrderAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return (_venues.Count == 0 ? 0 : _venues.Max(v => v.DisplayOrder)) + 1;
    }

    public async Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default)
    {
        await WithInner(inner => inner.CreateVenueAsync(venue, ct));
        await RefreshVenuesAsync(ct);
    }

    public async Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default)
    {
        await WithInner(inner => inner.UpdateVenueAsync(venue, ct));
        await RefreshVenuesAsync(ct);
        // Venue rename → refresh approved events to update flattened VenueName.
        await RefreshAllEventsAsync(ct);
    }

    public async Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.DeleteVenueAsync(id, ct));
        if (result.deleted)
            await RefreshVenuesAsync(ct);
        return result;
    }

    public async Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default)
    {
        await WithInner(inner => inner.MoveVenueAsync(id, direction, ct));
        await RefreshVenuesAsync(ct);
    }

    // ==========================================================================
    // Submissions — pass-through (submitter scope, infrequent)
    // ==========================================================================

    public Task<IReadOnlyList<EventInfo>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetUserSubmissionsAsync(userId, ct));

    public Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetUserEventAsync(eventId, userId, ct));

    public Task<IReadOnlyList<EventInfo>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetCampSubmissionsAsync(campId, ct));

    public Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetCampEventAsync(eventId, campId, ct));

    public async Task SubmitEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        await WithInner(inner => inner.SubmitEventAsync(guideEvent, ct));
        // Submission is Pending, not Approved — no approved-event cache change.
    }

    public async Task UpdateAndResubmitAsync(Event guideEvent, CancellationToken ct = default)
    {
        var wasApproved = guideEvent.Status == EventStatus.Approved;
        await WithInner(inner => inner.UpdateAndResubmitAsync(guideEvent, ct));
        // Resubmit transitions away from Approved → drop the cache entry.
        if (wasApproved)
            _eventCache.Invalidate(guideEvent.Id);
    }

    public async Task WithdrawEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        await WithInner(inner => inner.WithdrawEventAsync(guideEvent, ct));
        _eventCache.Invalidate(guideEvent.Id);
    }

    public async Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.BulkImportAsync(
            campId, submitterUserId, rows, gateOpeningDate, eventEndOffset, timeZone, ct));
        // Updates can transition Approved events away from Approved; rebuild the
        // approved-event projection wholesale. New events are Pending and never
        // enter the approved cache, so a create-only import needs no refresh.
        if (result.UpdatedCount > 0)
            await RefreshAllEventsAsync(ct);
        return result;
    }

    // ==========================================================================
    // Browse / API — cached snapshot with in-memory filter
    // ==========================================================================

    public async Task<IReadOnlyList<ApprovedEventView>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        var excluded = excludedSlugs?.Count > 0
            ? new HashSet<string>(excludedSlugs, StringComparer.Ordinal)
            : null;

        var results = new List<ApprovedEventView>();
        foreach (var view in _eventCache.Values)
        {
            if (excluded is not null && excluded.Contains(view.CategorySlug)) continue;
            if (categoryId.HasValue && view.CategoryId != categoryId.Value) continue;
            if (venueId.HasValue && view.GuideSharedVenueId != venueId.Value) continue;
            if (campId.HasValue && view.CampId != campId.Value) continue;
            if (!string.IsNullOrWhiteSpace(q) && !MatchesQuery(view, q)) continue;

            results.Add(view);
        }

        results.Sort((a, b) => a.StartAt.CompareTo(b.StartAt));
        return results;
    }

    public async Task<ApprovedEventView?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _eventCache.TryGet(id, out var view) ? view : null;
    }

    private static bool MatchesQuery(ApprovedEventView view, string q) =>
        view.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        view.Description.Contains(q, StringComparison.OrdinalIgnoreCase);

    // ==========================================================================
    // Favourites — per-user, pass-through (not in projection scope)
    // ==========================================================================

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetFavouriteEventIdsAsync(userId, ct));

    public Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetFavouritesWithEventsAsync(userId, ct));

    public Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default) =>
        WithInner(inner => inner.ToggleFavouriteAsync(userId, eventId, ct));

    public Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default) =>
        WithInner(inner => inner.AddFavouriteAsync(userId, eventId, ct));

    public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default) =>
        WithInner(inner => inner.RemoveFavouriteAsync(userId, eventId, ct));

    // ==========================================================================
    // Preferences — per-user, pass-through (not in projection scope)
    // ==========================================================================

    public Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetExcludedCategorySlugsAsync(userId, ct));

    public Task<EventPreferenceInfo?> GetPreferenceAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetPreferenceAsync(userId, ct));

    public Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default) =>
        WithInner(inner => inner.SavePreferenceAsync(userId, slugs, ct));

    // ==========================================================================
    // Moderation — fresh-count critical, route past cache
    // ==========================================================================

    public Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetEventStatusCountsAsync(ct));

    public Task<IReadOnlyList<EventInfo>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default) =>
        WithInner(inner => inner.GetEventsByStatusAsync(status, ct));

    public Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetEventForModerationAsync(eventId, ct));

    public Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetCampEventsForOverlapAsync(ct));

    public async Task ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason,
        CancellationToken ct = default)
    {
        await WithInner(inner => inner.ApplyModerationAsync(eventId, actorUserId, actionType, reason, ct));
        // Approve / reject / resubmit-request all transition the event's
        // approved-ness — re-load the single entry against the DB so the
        // cache reflects the post-moderation state (add for new approval,
        // remove for unapproved, no-op if it was never approved).
        await RefreshEventEntryAsync(eventId, ct);
    }

    // ==========================================================================
    // Dashboard / Export — moderator-only, must show fresh pending count
    // ==========================================================================

    public Task<IReadOnlyList<EventInfo>> GetAllEventsForDashboardAsync(CancellationToken ct = default) =>
        // STAYS DIRECT DB — moderation dashboard needs current pending/rejected
        // counts that the approved-only cache cannot answer.
        WithInner(inner => inner.GetAllEventsForDashboardAsync(ct));

    public Task<ApprovedEventsExportInfo> GetApprovedEventsForExportAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetApprovedEventsForExportAsync(ct));

    // ==========================================================================
    // IEventViewInvalidator — external invalidation hooks (issue #719)
    // ==========================================================================

    public Task InvalidateGuideSettingsAsync(CancellationToken ct = default) =>
        RefreshSettingsAsync(ct);

    // ==========================================================================
    // Warmup — composition forces the decorator to own IHostedService directly.
    // _isLoaded / _loadLock guard all four projections together (events dict +
    // categories + venues + settings); the inner _eventCache passes
    // warmOnStartup: false because that single-dict view is not the warmup
    // unit here.
    // ==========================================================================

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Warming Events cache at startup");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            logger.LogInformation(
                "Events cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Events cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to warm Events cache at startup; lazy reads will populate on demand");
        }
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task WarmAllAsync(CancellationToken ct = default)
    {
        if (_isLoaded) return;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_isLoaded) return;

            // Snapshot holds ALL rows (active + inactive) — admin Edit pages
            // and slug-uniqueness must see inactive rows, matching the DB.
            // Active-only filtering happens at projection time in
            // GetActiveCategoriesAsync / GetActiveVenuesAsync.
            var categories = await WithInner(inner => inner.GetAllCategoriesAsync(ct));
            var venues = await WithInner(inner => inner.GetAllVenuesAsync(ct));
            var settings = await WithInner(inner => inner.GetGuideSettingsAsync(ct));
            var approved = await WithInner(inner => inner.GetApprovedEventsAsync(null, null, null, null, [], ct));

            _categories = categories.Select(ManageInfoToCategoryView).ToList();
            _venues = venues.Select(ManageInfoToVenueView).ToList();
            _settings = settings;

            _eventCache.Clear();
            foreach (var ev in approved)
                _eventCache.Set(ev.Id, ev);

            _isLoaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_isLoaded) return;
        await WarmAllAsync(ct);
    }

    // ==========================================================================
    // Refresh helpers — surgical re-loads after writes
    // ==========================================================================

    private async Task<EventGuideSettingsView?> GetSettingsViewAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return _settings;
    }

    private async Task RefreshSettingsAsync(CancellationToken ct)
    {
        // The inner service stitches TimeZoneId from the Shifts-owned
        // event_settings row via IBurnSettingsService (#719) and returns the
        // ready EventGuideSettingsView; cache it directly.
        _settings = await WithInner(inner => inner.GetGuideSettingsAsync(ct));
    }

    private async Task RefreshCategoriesAsync(CancellationToken ct)
    {
        var categories = await WithInner(inner => inner.GetAllCategoriesAsync(ct));
        _categories = categories.Select(ManageInfoToCategoryView).ToList();
    }

    private async Task RefreshVenuesAsync(CancellationToken ct)
    {
        var venues = await WithInner(inner => inner.GetAllVenuesAsync(ct));
        _venues = venues.Select(ManageInfoToVenueView).ToList();
    }

    private async Task RefreshEventEntryAsync(Guid eventId, CancellationToken ct)
    {
        var ev = await WithInner(inner => inner.GetApprovedEventByIdAsync(eventId, ct));
        if (ev is null)
        {
            _eventCache.Invalidate(eventId);
            return;
        }

        _eventCache.Set(eventId, ev);
    }

    private async Task RefreshAllEventsAsync(CancellationToken ct)
    {
        // If the cache hasn't been warmed yet (rare write-before-warmup race),
        // do a full WarmAllAsync rather than a partial event-only refresh —
        // otherwise _categories/_venues/_settings stay in their empty
        // constructor state and _isLoaded stays false, forcing a redundant
        // re-warmup on the very next read. WarmAllAsync sets _isLoaded = true.
        if (!_isLoaded)
        {
            await WarmAllAsync(ct);
            return;
        }

        var approved = await WithInner(inner => inner.GetApprovedEventsAsync(null, null, null, null, [], ct));

        // Rebuild the whole approved set — category / venue renames touch
        // every event row's flattened fields.
        var fresh = new ConcurrentDictionary<Guid, ApprovedEventView>();
        foreach (var ev in approved)
            fresh[ev.Id] = ev;

        // Replace via clear + set rather than swapping the underlying dict —
        // TrackedCache owns its dict and exposes only Clear / Set / Invalidate.
        _eventCache.Clear();
        foreach (var (id, view) in fresh)
            _eventCache.Set(id, view);
    }

    // ==========================================================================
    // Projection helpers
    // ==========================================================================

    private static EventCategoryView ManageInfoToCategoryView(EventCategoryManageInfo c) => new(
        Id: c.Id,
        Name: c.Name,
        Slug: c.Slug,
        IsSensitive: c.IsSensitive,
        DisplayOrder: c.DisplayOrder,
        IsActive: c.IsActive);

    private static EventVenueView ManageInfoToVenueView(EventVenueManageInfo v) => new(
        Id: v.Id,
        Name: v.Name,
        Description: v.Description,
        LocationDescription: v.LocationDescription,
        DisplayOrder: v.DisplayOrder,
        IsActive: v.IsActive);

    // ==========================================================================
    // Inner-service plumbing
    // ==========================================================================

    private async Task<T> WithInner<T>(Func<IEventService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEventService>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInner(Func<IEventService, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEventService>(InnerServiceKey);
        await work(inner);
    }
}
