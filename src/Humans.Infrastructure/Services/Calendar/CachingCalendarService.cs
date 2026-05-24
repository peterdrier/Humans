using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Calendar;
using Humans.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Calendar;

/// <summary>
/// Singleton cache-backed calendar read service. Holds every non-soft-deleted
/// <c>CalendarEventInfo</c> row with embedded exceptions, keyed by event id.
/// Write methods delegate to the keyed inner service and refresh the read cache.
/// </summary>
public sealed class CachingCalendarService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingCalendarService> logger)
    : TrackedCache<Guid, CalendarEventInfo>("Calendar.Event", warmOnStartup: true, logger),
        ICalendarService,
        ICalendarServiceRead
{
    /// <summary>DI key for the undecorated inner <see cref="ICalendarService"/>.</summary>
    public const string InnerServiceKey = "calendar-inner";

    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);

        var matched = CalendarOccurrenceExpander.FilterForWindow(
            Snapshot().Select(kvp => kvp.Value),
            from, to, teamId);

        var teamNames = await ResolveTeamNamesAsync(matched, ct);
        return CalendarOccurrenceExpander.Expand(matched, from, to, teamNames, logger);
    }

    public async Task<CalendarEventDetail?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        var info = await GetAsync(id, ct);
        if (info is null) return null;

        return new CalendarEventDetail(
            info.Id,
            info.Title,
            info.Description,
            info.Location,
            info.LocationUrl,
            info.OwningTeamId,
            info.StartUtc,
            info.EndUtc,
            info.IsAllDay,
            info.RecurrenceRule,
            info.RecurrenceTimezone,
            info.CreatedAt,
            info.UpdatedAt);
    }

    public async Task<IReadOnlyList<CalendarEventInfo>> GetAllEventInfosAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary.Values.ToList();
    }

    public async Task<CalendarEventInfo?> GetEventInfoAsync(Guid id, CancellationToken ct = default) =>
        await GetAsync(id, ct);

    public async Task<CalendarEvent> CreateEventAsync(
        CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.CreateEventAsync(dto, createdByUserId, ct));
        await ReplaceAsync(result.Id, ct);
        return result;
    }

    public async Task<CalendarEventMutationResult> CreateEventWithResultAsync(
        CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.CreateEventWithResultAsync(dto, createdByUserId, ct));
        if (result.Succeeded && result.Event is not null)
            await ReplaceAsync(result.Event.Id, ct);
        return result;
    }

    public async Task<CalendarEvent> UpdateEventAsync(
        Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.UpdateEventAsync(id, dto, updatedByUserId, ct));
        await ReplaceAsync(id, ct);
        return result;
    }

    public async Task<CalendarEventMutationResult> UpdateEventWithResultAsync(
        Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.UpdateEventWithResultAsync(id, dto, updatedByUserId, ct));
        if (result.Succeeded)
            await ReplaceAsync(id, ct);
        return result;
    }

    public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.DeleteEventAsync(id, deletedByUserId, ct));
        await ReplaceAsync(id, ct);
    }

    public async Task CancelOccurrenceAsync(
        Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.CancelOccurrenceAsync(eventId, originalOccurrenceStartUtc, userId, ct));
        await ReplaceAsync(eventId, ct);
    }

    public async Task OverrideOccurrenceAsync(
        Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto,
        Guid userId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.OverrideOccurrenceAsync(eventId, originalOccurrenceStartUtc, dto, userId, ct));
        await ReplaceAsync(eventId, ct);
    }

    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var events = await WithInner(inner => inner.GetAllEventInfosAsync(ct));
        foreach (var ev in events)
        {
            if (ContainsKey(ev.Id)) continue;
            Set(ev.Id, ev);
        }
    }

    protected override async ValueTask<CalendarEventInfo?> LoadRowAsync(Guid key, CancellationToken ct) =>
        await WithInner(inner => inner.GetEventInfoAsync(key, ct));

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveTeamNamesAsync(
        IReadOnlyList<CalendarEventInfo> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return new Dictionary<Guid, string>();

        var teamIds = events.Select(e => e.OwningTeamId).Distinct().ToList();
        await using var scope = scopeFactory.CreateAsyncScope();
        var teamService = scope.ServiceProvider.GetRequiredService<ITeamServiceRead>();
        var teamsById = await teamService.GetTeamsAsync(ct);
        return teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
    }

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
