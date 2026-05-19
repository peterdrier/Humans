using Humans.Application.DTOs.Calendar;
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
/// Singleton caching decorator for <see cref="ICalendarService"/>. Holds every
/// non-soft-deleted <c>calendar_events</c> row with its <c>Exceptions</c>
/// embedded, keyed by event id. Exception writes evict the parent.
/// </summary>
public sealed class CachingCalendarService(
    ICalendarRepository repo,
    IServiceScopeFactory scopeFactory,
    ILogger<CachingCalendarService> logger)
    : TrackedCache<Guid, CalendarEventInfo>("Calendar.Event", warmOnStartup: true, logger), ICalendarService
{
    /// <summary>DI key for the undecorated inner <see cref="ICalendarService"/>.</summary>
    public const string InnerServiceKey = "calendar-inner";

    // Reads

    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);

        // Snapshot-scan: filter the dict by the same predicate the SQL prefilter uses.
        var snapshot = Snapshot();
        var matched = CalendarOccurrenceExpander.FilterForWindow(
            snapshot.Select(kvp => kvp.Value),
            from, to, teamId);

        var teamNames = await ResolveTeamNamesAsync(matched, ct);

        return CalendarOccurrenceExpander.Expand(matched, from, to, teamNames, logger);
    }

    public async Task<CalendarEventDetail?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        var info = await GetAsync(id, ct);
        if (info is null) return null;

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
        await using var scope = scopeFactory.CreateAsyncScope();
        var teamService = scope.ServiceProvider.GetRequiredService<ITeamService>();
        var teamsById = await teamService.GetTeamsAsync(ct);
        return teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
    }

    // Writes — delegate then refresh

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
        // Exception writes evict the PARENT — Exceptions list is embedded in CalendarEventInfo.
        await WithInner(inner => inner.CancelOccurrenceAsync(
            eventId, originalOccurrenceStartUtc, userId, ct));
        await InvalidateEventAsync(eventId, ct);
    }

    public async Task OverrideOccurrenceAsync(
        Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto,
        Guid userId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.OverrideOccurrenceAsync(
            eventId, originalOccurrenceStartUtc, dto, userId, ct));
        await InvalidateEventAsync(eventId, ct);
    }

    private Task InvalidateEventAsync(Guid eventId, CancellationToken ct) =>
        ReplaceAsync(eventId, ct);

    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var events = await repo.GetAllAsync(ct);
        foreach (var ev in events)
        {
            // Skip if a concurrent post-commit invalidate already wrote a fresher row (PR #585 race).
            if (ContainsKey(ev.Id)) continue;
            Set(ev.Id, CalendarOccurrenceExpander.ToInfo(ev));
        }
    }

    protected override async ValueTask<CalendarEventInfo?> LoadRowAsync(Guid key, CancellationToken ct)
    {
        var ev = await repo.GetEventByIdAsync(key, ct);
        return ev is null ? null : CalendarOccurrenceExpander.ToInfo(ev);
    }

    // Inner-resolution

    private async Task<TResult> WithInner<TResult>(Func<ICalendarService, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICalendarService>(InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInner(Func<ICalendarService, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICalendarService>(InnerServiceKey);
        await action(inner);
    }
}
