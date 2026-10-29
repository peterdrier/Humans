using System.Collections.Concurrent;
using System.Diagnostics;
using Humans.Events.Services.Dtos;
using Humans.Base.Caching;
using Humans.Base.Extensions;
using Humans.Base.Interfaces.Caching;
using Humans.Settings.Contracts;
using Humans.Base.Threading;
using Humans.Events.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Humans.Events.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.Events.Services;

/// <summary>
/// Caches approved events, categories, venues and guide settings. Every write delegates to the inner service, then
/// refreshes the affected slice. The moderation dashboard reads past the cache: it needs a live pending count.
/// </summary>
internal sealed class CachingEventService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingEventService> logger)
    : IEventManagement, IUserDataContributor, IUserMerge, IEventSettingsChangeListener, IHostedService
{
    public const string InnerServiceKey = "event-inner";

    // warmOnStartup: false: the decorator warms all four projections together, which a per-cache kickoff can't.
    private readonly TrackedCache<Guid, ApprovedEventView> _eventCache = new(
        "Event.ApprovedEventView", warmOnStartup: false, logger);

    // Swapped atomically under _loadLock.
    private volatile IReadOnlyList<EventCategoryView> _categories = [];
    private volatile IReadOnlyList<EventVenueView> _venues = [];
    private volatile EventGuideSettingsView? _settings;

    // A flag rather than nulling _settings, because null is also the legitimate "no guide settings row" answer.
    private volatile bool _settingsStale;

    private readonly TrackedLock _loadLock = new("CachingEventService.Load");
    private readonly TrackedLock _settingsLock = new("CachingEventService.Settings");
    private volatile bool _isLoaded;

    public ICacheStats EventCacheStats => _eventCache;

    public Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default) =>
        GetSettingsViewAsync(ct);

    public Task<IReadOnlyList<EventSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetEventSettingsOptionsAsync(ct));

    public Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetEventSettingsByIdAsync(id, ct));

    public Task<bool> SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            var saved = await WithInnerAsync(inner => inner.SaveGuideSettingsAsync(
                existingId, eventSettingsId, submissionOpenAt, submissionCloseAt, guidePublishAt,
                maxPrintSlots, ct));
            if (saved) await RefreshSettingsAsync(ct);
            return saved;
        });

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
        // The inner method includes .Events for management counts, which the cache doesn't carry.
        return await WithInnerAsync(inner => inner.GetAllCategoriesAsync(ct));
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

    public Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.CreateCategoryAsync(category, ct));
            await RefreshCategoriesAsync(ct);
        });

    public Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.UpdateCategoryAsync(category, ct));
            await RefreshCategoriesAsync(ct);
            // A rename or sensitivity flip changes flattened fields on every approved event.
            await RefreshAllEventsAsync(ct);
        });

    public Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.DeleteCategoryAsync(id, ct));
            if (result.deleted)
                await RefreshCategoriesAsync(ct);
            return result;
        });

    public Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.MoveCategoryAsync(id, direction, ct));
            await RefreshCategoriesAsync(ct);
        });

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
        // The inner method includes .Events for management counts.
        return await WithInnerAsync(inner => inner.GetAllVenuesAsync(ct));
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

    public Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.CreateVenueAsync(venue, ct));
            await RefreshVenuesAsync(ct);
        });

    public Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.UpdateVenueAsync(venue, ct));
            await RefreshVenuesAsync(ct);
            // A rename changes the flattened VenueName on approved events.
            await RefreshAllEventsAsync(ct);
        });

    public Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.DeleteVenueAsync(id, ct));
            if (result.deleted)
                await RefreshVenuesAsync(ct);
            return result;
        });

    public Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.MoveVenueAsync(id, direction, ct));
            await RefreshVenuesAsync(ct);
        });

    public Task<IReadOnlyList<EventInfo>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetUserSubmissionsAsync(userId, ct));

    public Task<EventInfo?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetUserEventAsync(eventId, userId, ct));

    public Task<IReadOnlyList<EventInfo>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetCampSubmissionsAsync(campId, ct));

    public Task<EventInfo?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetCampEventAsync(eventId, campId, ct));

    public Task<CampSubmissionsSummary> GetCampSubmissionsSummaryAsync(Guid campId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetCampSubmissionsSummaryAsync(campId, ct));

    public Task<byte[]> BuildBulkUploadTemplateAsync(Guid campId, string campName, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.BuildBulkUploadTemplateAsync(campId, campName, ct));

    public Task<EventSaveResult> SubmitEventAsync(Event guideEvent, string? lifecycleActionUrl = null, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.SubmitEventAsync(guideEvent, lifecycleActionUrl, ct));

    public Task<EventSaveResult> UpdateAndResubmitAsync(Guid eventId, EventEdit edit, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.UpdateAndResubmitAsync(eventId, edit, ct));
            // A successful resubmit never leaves the event Approved; a refusal preserves it.
            if (result.ErrorKey is null) _eventCache.Invalidate(eventId);
            return result;
        });

    public Task WithdrawEventAsync(Guid eventId, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.WithdrawEventAsync(eventId, ct));
            _eventCache.Invalidate(eventId);
        });

    public Task<bool> ModeratorWithdrawAsync(Guid eventId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            var withdrawn = await WithInnerAsync(inner => inner.ModeratorWithdrawAsync(eventId, actorUserId, ct));
            _eventCache.Invalidate(eventId);
            return withdrawn;
        });

    public Task AdminUpdateAsync(Guid eventId, EventEdit edit, Guid actorUserId, string? note, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.AdminUpdateAsync(eventId, edit, actorUserId, note, ct));
            // Status is preserved, so an edited Approved event stays approved with changed flattened fields.
            await RefreshEventEntryAsync(eventId, ct);
        });

    public Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.BulkImportAsync(
                campId, submitterUserId, rows, gateOpeningDate, eventEndOffset, timeZone, ct));
            // Updates can move Approved events out of Approved. New events are Pending and never enter the cache.
            if (result.UpdatedCount > 0)
                await RefreshAllEventsAsync(ct);
            return result;
        });

    public async Task<IReadOnlyList<ApprovedEventView>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        var excluded = excludedSlugs?.Count > 0
            ? new HashSet<string>(excludedSlugs, StringComparer.Ordinal)
            : null;

        var trimmed = q?.Trim();
        var results = new List<ApprovedEventView>();
        foreach (var view in _eventCache.Values)
        {
            if (excluded is not null && excluded.Contains(view.CategorySlug)) continue;
            if (categoryId.HasValue && view.CategoryId != categoryId.Value) continue;
            if (venueId.HasValue && view.GuideSharedVenueId != venueId.Value) continue;
            if (campId.HasValue && view.CampId != campId.Value) continue;
            if (!string.IsNullOrEmpty(trimmed) && !MatchesQuery(view, trimmed)) continue;

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

    // Served entirely from the cache: search must never hit the database.
    public async Task<IReadOnlyList<EventSearchHit>> SearchAsync(
        string query, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return [];

        await EnsureLoadedAsync(ct);

        var trimmed = query.Trim();

        // A pasted GUID jumps straight to that event.
        if (Guid.TryParse(trimmed, out var id))
        {
            return _eventCache.TryGet(id, out var byId)
                ? [new EventSearchHit(byId.Id, byId.Title, StringSearchExtensions.ExactNameScore)]
                : [];
        }

        return _eventCache.Values
            .Where(e => MatchesQuery(e, trimmed))
            // Deterministic Take(max); the caller re-ranks by score.
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(e => new EventSearchHit(e.Id, e.Title, ScoreEventMatch(e, trimmed)))
            .ToList();
    }

    /// <summary>
    /// Title: 100/80/60 (exact/prefix/contains). Description only: half that, so it ranks below every Title match
    /// while description-only rows still order sensibly instead of tying.
    /// </summary>
    private static int ScoreEventMatch(ApprovedEventView view, string query)
    {
        var titleScore = view.Title.NameMatchScore(query);
        return titleScore > 0 ? titleScore : view.Description.NameMatchScore(query) / 2;
    }

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetFavouriteEventIdsAsync(userId, ct));

    public Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetFavouritesWithEventsAsync(userId, ct));

    public Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.AddFavouriteAsync(userId, eventId, dayOffset, ct));

    public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.RemoveFavouriteAsync(userId, eventId, dayOffset, ct));

    public Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetExcludedCategorySlugsAsync(userId, ct));

    public Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.SavePreferenceAsync(userId, slugs, ct));

    public Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetEventStatusCountsAsync(ct));

    public Task<IReadOnlyList<EventInfo>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetEventsByStatusAsync(status, ct));

    public Task<EventInfo?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetEventForModerationAsync(eventId, ct));

    public Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetCampEventsForOverlapAsync(ct));

    public Task<ModerationOutcome> ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason,
        string? submitterEditUrl = null, CancellationToken ct = default) =>
        MutateAsync(async () =>
        {
            var outcome = await WithInnerAsync(inner => inner.ApplyModerationAsync(eventId, actorUserId, actionType, reason, submitterEditUrl, ct));
            if (outcome is not ModerationOutcome.Applied)
                return outcome;
            // Every moderation action can change approved-ness; reload the one entry from the database.
            await RefreshEventEntryAsync(eventId, ct);
            return outcome;
        });

    public Task<IReadOnlyList<EventInfo>> GetAllEventsForDashboardAsync(CancellationToken ct = default) =>
        // Stays on the database: the approved-only cache cannot answer pending/rejected counts.
        WithInnerAsync(inner => inner.GetAllEventsForDashboardAsync(ct));

    public Task<byte[]> GetApprovedEventsForExportAsync(Guid actorUserId, CancellationToken ct = default) =>
        // Every CSV disclosure must audit, including repeated downloads of unchanged data.
        WithInnerAsync(inner => inner.GetApprovedEventsForExportAsync(actorUserId, ct));

    /// <summary>
    /// Marks the projection stale rather than refreshing inline: the seam is a synchronous void, and the next read
    /// reloads. Without it a time zone edit in Settings showed the old zone until restart.
    /// </summary>
    public void EventSettingsChanged(Guid eventSettingsId) => _settingsStale = true;

    // Carried by the decorator: erasure clears the person's Host name on events that stay cached.

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => EventService.Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync(inner => inner.ContributeForUserAsync(userId, ct));

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.EraseForUserAsync(userId, ct));
            await RefreshAllEventsAsync(ct);
        });

    // Carried by the decorator: the fold re-points SubmitterUserId on cached approved events.

    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));
            await RefreshAllEventsAsync(ct);
        });

    // _isLoaded / _loadLock guard all four projections together.

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

        using var _ = await _loadLock.AcquireAsync(logger, ct);
        if (_isLoaded) return;

        // Holds inactive rows too: admin edit pages and slug-uniqueness must see them. Active filtering happens at
        // projection time.
        var categories = await WithInnerAsync(inner => inner.GetAllCategoriesAsync(ct));
        var venues = await WithInnerAsync(inner => inner.GetAllVenuesAsync(ct));
        var approved = await WithInnerAsync(inner => inner.GetApprovedEventsAsync(null, null, null, null, [], ct));

        _categories = categories.Select(ManageInfoToCategoryView).ToList();
        _venues = venues.Select(ManageInfoToVenueView).ToList();
        await RefreshSettingsAsync(ct);

        _eventCache.Clear();
        foreach (var ev in approved)
            _eventCache.Set(ev.Id, ev);

        _isLoaded = true;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_isLoaded) return;
        await WarmAllAsync(ct);
    }

    private async Task<EventGuideSettingsView?> GetSettingsViewAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        await RefreshSettingsAsync(ct, onlyIfStale: true);
        return _settings;
    }

    private async Task RefreshSettingsAsync(CancellationToken ct, bool onlyIfStale = false)
    {
        using var gate = await _settingsLock.AcquireAsync(logger, ct);
        if (onlyIfStale && !_settingsStale) return;

        // Consume the flag before fetching so changes during the read still trigger
        // another reload. Serialize all settings loads so an older one cannot finish last.
        _settingsStale = false;
        try
        {
            _settings = await WithInnerAsync(inner => inner.GetGuideSettingsAsync(ct));
        }
        catch
        {
            _settingsStale = true;
            throw;
        }
    }

    private async Task RefreshCategoriesAsync(CancellationToken ct)
    {
        var categories = await WithInnerAsync(inner => inner.GetAllCategoriesAsync(ct));
        _categories = categories.Select(ManageInfoToCategoryView).ToList();
    }

    private async Task RefreshVenuesAsync(CancellationToken ct)
    {
        var venues = await WithInnerAsync(inner => inner.GetAllVenuesAsync(ct));
        _venues = venues.Select(ManageInfoToVenueView).ToList();
    }

    private async Task RefreshEventEntryAsync(Guid eventId, CancellationToken ct)
    {
        var ev = await WithInnerAsync(inner => inner.GetApprovedEventByIdAsync(eventId, ct));
        if (ev is null)
        {
            _eventCache.Invalidate(eventId);
            return;
        }

        _eventCache.Set(eventId, ev);
    }

    private async Task RefreshAllEventsAsync(CancellationToken ct)
    {
        // Not warmed yet (a write-before-warmup race): warm everything, or the other slices stay empty and the next
        // read re-warms.
        if (!_isLoaded)
        {
            await WarmAllAsync(ct);
            return;
        }

        var approved = await WithInnerAsync(inner => inner.GetApprovedEventsAsync(null, null, null, null, [], ct));

        // Rebuild the whole set: a category or venue rename touches every row's flattened fields.
        var fresh = new ConcurrentDictionary<Guid, ApprovedEventView>();
        foreach (var ev in approved)
            fresh[ev.Id] = ev;

        // TrackedCache owns its dict and exposes only Clear / Set / Invalidate.
        _eventCache.Clear();
        foreach (var (id, view) in fresh)
            _eventCache.Set(id, view);
    }

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

    // Include the write and its projection refresh in one failure boundary. Either can
    // fail after a commit; the next cached read must reload all four slices together.
    private async Task<T> MutateAsync<T>(Func<Task<T>> mutation)
    {
        try
        {
            return await mutation();
        }
        catch
        {
            _isLoaded = false;
            throw;
        }
    }

    private async Task MutateAsync(Func<Task> mutation)
    {
        try
        {
            await mutation();
        }
        catch
        {
            _isLoaded = false;
            throw;
        }
    }

    private async Task<T> WithInnerAsync<T>(Func<IEventManagement, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEventManagement>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInnerAsync(Func<IEventManagement, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEventManagement>(InnerServiceKey);
        await work(inner);
    }
}
