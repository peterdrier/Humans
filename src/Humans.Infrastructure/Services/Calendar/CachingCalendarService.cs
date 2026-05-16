using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Calendar;
using Humans.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Calendar;

/// <summary>
/// Singleton caching decorator for <see cref="ICalendarService"/>. Owns the
/// canonical <see cref="CalendarEventInfo"/> read-model — every non-soft-deleted
/// <c>calendar_events</c> row, with its <c>Exceptions</c> collection embedded,
/// keyed by event id. Mirrors the Teams (decorator-mediated, no interceptor)
/// pattern from <c>CachingTeamService</c>. Cache-migration plan task T-08.
/// </summary>
/// <remarks>
/// <para>
/// Reads:
/// <list type="bullet">
///   <item><see cref="GetEventByIdAsync"/> — direct dict lookup; cache hit
///     synchronously projects to <see cref="CalendarEventDetail"/>. Cache
///     miss falls through to the inner service.</item>
///   <item><see cref="GetOccurrencesInWindowAsync"/> — snapshot-scan window
///     query (analog of <c>CachingShiftViewService.InvalidateShift</c>'s
///     snapshot walk). Filters the in-memory dict by the same predicate the
///     SQL prefilter uses, then delegates expansion to the pure
///     <see cref="CalendarOccurrenceExpander"/>. No round-trip to SQL on
///     hit.</item>
/// </list>
/// </para>
/// <para>
/// Writes — all five mutation methods on <see cref="ICalendarService"/>
/// (<c>CreateEventAsync</c>, <c>UpdateEventAsync</c>, <c>DeleteEventAsync</c>,
/// <c>CancelOccurrenceAsync</c>, <c>OverrideOccurrenceAsync</c>) delegate to
/// the inner service via <see cref="IServiceScopeFactory"/>, then call
/// <see cref="InvalidateEvent"/> with the affected event id.
/// </para>
/// <para>
/// <b>Exception writes evict the PARENT event</b>: the per-occurrence write
/// methods (<c>CancelOccurrenceAsync</c> / <c>OverrideOccurrenceAsync</c>)
/// upsert into the <c>calendar_event_exceptions</c> child table, but the
/// cache is keyed by parent event id and embeds the <c>Exceptions</c> list
/// inside <see cref="CalendarEventInfo"/>. <see cref="InvalidateEvent"/>
/// refreshes the parent entry (which re-loads its full <c>Exceptions</c>
/// list from the repository) — there is no separate exception cache row.
/// </para>
/// <para>
/// Future load: an iCal feed endpoint (planned — <c>User.ICalToken</c>
/// already exists on <c>UserInfo</c> but is unused today) will read the
/// same window-expansion path; the snapshot-scan design absorbs that
/// traffic without a second cache.
/// </para>
/// <para>
/// Registered as Singleton; resolves the Scoped inner <see cref="ICalendarService"/>
/// (and <see cref="ITeamService"/> for team-name stitching) per call via
/// <see cref="IServiceScopeFactory"/>. <see cref="ICalendarRepository"/> is
/// Singleton (IDbContextFactory-based), injected directly.
/// </para>
/// </remarks>
public sealed class CachingCalendarService : TrackedCache<Guid, CalendarEventInfo>, ICalendarService
{
    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="ICalendarService"/>
    /// is registered. The Singleton decorator resolves the Scoped inner via this
    /// key per call so the unkeyed <see cref="ICalendarService"/> registration
    /// (which maps to this Singleton) does not self-resolve.
    /// </summary>
    public const string InnerServiceKey = "calendar-inner";

    private readonly ICalendarRepository _repo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CachingCalendarService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _isLoaded;

    public bool IsWarmedUp => _isLoaded;

    public CachingCalendarService(
        ICalendarRepository repo,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingCalendarService> logger)
        : base("Calendar.CalendarEventInfo")
    {
        _repo = repo;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        // Snapshot-scan analog of CachingShiftViewService.InvalidateShift /
        // GetUsersAsync — filter the in-memory dict by the same predicate the
        // SQL prefilter uses, then delegate expansion to the pure expander.
        var snapshot = Snapshot();
        var matched = CalendarOccurrenceExpander.FilterForWindow(
            snapshot.Select(kvp => kvp.Value),
            from, to, teamId);

        var teamNames = await ResolveTeamNamesAsync(matched, ct);

        return CalendarOccurrenceExpander.Expand(matched, from, to, teamNames, _logger);
    }

    public async Task<CalendarEventDetail?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        if (!TryGet(id, out var info))
        {
            // Cache may have been invalidated; fall through to inner once.
            return await WithInner(inner => inner.GetEventByIdAsync(id, ct));
        }

        return new CalendarEventDetail(
            Id: info.Id,
            Title: info.Title,
            Description: info.Description,
            Location: info.Location,
            LocationUrl: info.LocationUrl,
            OwningTeamId: info.OwningTeamId,
            StartUtc: info.StartUtc,
            EndUtc: info.EndUtc,
            IsAllDay: info.IsAllDay,
            RecurrenceRule: info.RecurrenceRule,
            RecurrenceTimezone: info.RecurrenceTimezone,
            CreatedAt: info.CreatedAt,
            UpdatedAt: info.UpdatedAt);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveTeamNamesAsync(
        IReadOnlyList<CalendarEventInfo> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return new Dictionary<Guid, string>();

        var teamIds = events.Select(e => e.OwningTeamId).Distinct().ToList();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var teamService = scope.ServiceProvider.GetRequiredService<ITeamService>();
        var teamsById = await teamService.GetTeamsAsync(ct);
        return teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
    }

    // ==========================================================================
    // Writes — delegate to inner, then refresh affected event's cache entry
    // ==========================================================================

    public async Task<CalendarEvent> CreateEventAsync(
        CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.CreateEventAsync(dto, createdByUserId, ct));
        await InvalidateEventAsync(result.Id, ct);
        return result;
    }

    public async Task<CalendarEventMutationResult> CreateEventWithResultAsync(
        CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.CreateEventWithResultAsync(dto, createdByUserId, ct));
        if (result.Succeeded && result.Event is not null)
            await InvalidateEventAsync(result.Event.Id, ct);
        return result;
    }

    public async Task<CalendarEvent> UpdateEventAsync(
        Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.UpdateEventAsync(id, dto, updatedByUserId, ct));
        await InvalidateEventAsync(id, ct);
        return result;
    }

    public async Task<CalendarEventMutationResult> UpdateEventWithResultAsync(
        Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.UpdateEventWithResultAsync(id, dto, updatedByUserId, ct));
        if (result.Succeeded)
            await InvalidateEventAsync(id, ct);
        return result;
    }

    public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.DeleteEventAsync(id, deletedByUserId, ct));
        await InvalidateEventAsync(id, ct);
    }

    public async Task CancelOccurrenceAsync(
        Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default)
    {
        // Exception writes evict the PARENT event entry — not a separate
        // exception row. The cache key is the event id; the next read
        // re-fetches the parent with its refreshed Exceptions list.
        await WithInner(inner => inner.CancelOccurrenceAsync(
            eventId, originalOccurrenceStartUtc, userId, ct));
        await InvalidateEventAsync(eventId, ct);
    }

    public async Task OverrideOccurrenceAsync(
        Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto,
        Guid userId, CancellationToken ct = default)
    {
        // Exception writes evict the PARENT event entry — see CancelOccurrenceAsync.
        await WithInner(inner => inner.OverrideOccurrenceAsync(
            eventId, originalOccurrenceStartUtc, dto, userId, ct));
        await InvalidateEventAsync(eventId, ct);
    }

    // ==========================================================================
    // Invalidation
    // ==========================================================================

    /// <summary>
    /// Single invalidation entry point. Every write path on this decorator
    /// calls it after delegating to the inner service. Re-fetches the event
    /// from the repository:
    /// <list type="bullet">
    ///   <item>Row present (not soft-deleted) → upsert refreshed
    ///     <see cref="CalendarEventInfo"/> with its current
    ///     <c>Exceptions</c> list.</item>
    ///   <item>Row absent (soft-deleted or never existed) → remove the
    ///     entry from the cache.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Per-occurrence writes (cancel/override) also flow through this method —
    /// the cache holds parent events with embedded exceptions, so an exception
    /// upsert evicts and refreshes the <em>parent</em>, not a separate
    /// exception row.
    /// </remarks>
    private async Task InvalidateEventAsync(Guid eventId, CancellationToken ct)
    {
        var fresh = await _repo.GetEventByIdAsync(eventId, ct);
        if (fresh is null)
        {
            Invalidate(eventId);
            return;
        }

        Set(eventId, CalendarOccurrenceExpander.ToInfo(fresh));
    }

    // ==========================================================================
    // Warmup
    // ==========================================================================

    public async Task WarmAllAsync(CancellationToken ct = default)
    {
        if (_isLoaded) return;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_isLoaded) return;

            var events = await _repo.GetAllAsync(ct);
            foreach (var ev in events)
                Set(ev.Id, CalendarOccurrenceExpander.ToInfo(ev));

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
    // Inner-resolution
    // ==========================================================================

    private async Task<TResult> WithInner<TResult>(Func<ICalendarService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICalendarService>(InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInner(Func<ICalendarService, Task> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICalendarService>(InnerServiceKey);
        await action(inner);
    }
}
