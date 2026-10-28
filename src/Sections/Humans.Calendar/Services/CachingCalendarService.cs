using Humans.Calendar.Services.Dtos;
using Humans.Base.Caching;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Calendar.Services;

internal sealed class CachingCalendarService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingCalendarService> logger)
    : TrackedCache<Guid, CalendarEventInfo>("Calendar.Event", warmOnStartup: true, logger),
        ICalendarQueries, ICalendarService
{
    /// <summary>DI key for the undecorated inner <see cref="ICalendarService"/>.</summary>
    public const string InnerServiceKey = "calendar-inner";

    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, DateTimeZone viewerZone, Guid? teamId = null, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return await ComposeOccurrencesAsync(Snapshot().Select(kvp => kvp.Value).ToList(), from, to, viewerZone, teamId, ct);
    }

    public Task<IReadOnlyList<CalendarOccurrence>> ComposeOccurrencesAsync(
        IReadOnlyList<CalendarEventInfo> events, Instant from, Instant to, DateTimeZone viewerZone,
        Guid? teamId = null, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.ComposeOccurrencesAsync(events, from, to, viewerZone, teamId, ct));

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
            info.UpdatedAt,
            info.StartDate,
            info.EndDateExclusive);
    }

    public async Task<IReadOnlyList<CalendarEventInfo>> GetAllEventInfosAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary.Values.ToList();
    }

    public async Task<CalendarEventInfo?> GetEventInfoAsync(Guid id, CancellationToken ct = default) =>
        await GetAsync(id, ct);

    public async Task<CalendarEventMutationResult> CreateEventWithResultAsync(
        CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        var result = await MutateWithResultAsync(inner => inner.CreateEventWithResultAsync(dto, createdByUserId, ct));
        if (result.Succeeded && result.Event is not null)
            await ReplaceAsync(result.Event.Id, CancellationToken.None);
        return result;
    }

    public async Task<CalendarEventMutationResult> UpdateEventWithResultAsync(
        Guid id, CreateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        var result = await MutateWithResultAsync(inner => inner.UpdateEventWithResultAsync(id, dto, updatedByUserId, ct));
        if (result.Succeeded)
            await ReplaceAsync(id, CancellationToken.None);
        return result;
    }

    public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.DeleteEventAsync(id, deletedByUserId, ct));
        await ReplaceAsync(id, CancellationToken.None);
    }

    public Task<bool> CancelOccurrenceAsync(
        Guid eventId, Instant? originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null) =>
        MutateOccurrenceAsync(eventId, inner => inner.CancelOccurrenceAsync(eventId, originalOccurrenceStartUtc, userId, ct, originalDate));

    public Task<bool> OverrideOccurrenceAsync(
        Guid eventId, Instant? originalOccurrenceStartUtc, OverrideOccurrenceDto dto,
        Guid userId, CancellationToken ct = default, LocalDate? originalDate = null) =>
        MutateOccurrenceAsync(eventId, inner => inner.OverrideOccurrenceAsync(eventId, originalOccurrenceStartUtc, dto, userId, ct, originalDate));

    private async Task<bool> MutateOccurrenceAsync(Guid eventId, Func<ICalendarService, Task<bool>> action)
    {
        var completed = false;
        try
        {
            var saved = await WithInnerAsync(action);
            if (saved) await ReplaceAsync(eventId, CancellationToken.None);
            completed = saved;
            return saved;
        }
        finally
        {
            if (!completed) Clear();
        }
    }

    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var events = await WithInnerAsync(inner => inner.GetAllEventInfosAsync(ct));
        foreach (var ev in events)
        {
            if (ContainsKey(ev.Id)) continue;
            Set(ev.Id, ev);
        }
    }

    protected override async ValueTask<CalendarEventInfo?> LoadRowAsync(Guid key, CancellationToken ct) =>
        await WithInnerAsync(inner => inner.GetEventInfoAsync(key, ct));

    // A failed save may already have committed, so the old snapshot can't be trusted: the next read reloads all rows.
    private async Task<CalendarEventMutationResult> MutateWithResultAsync(
        Func<ICalendarService, Task<CalendarEventMutationResult>> action)
    {
        var clear = true;
        try
        {
            var result = await WithInnerAsync(action);
            clear = !result.Succeeded;
            return result;
        }
        finally
        {
            if (clear) Clear();
        }
    }

    private async Task<TResult> WithInnerAsync<TResult>(Func<ICalendarService, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICalendarService>(InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInnerAsync(Func<ICalendarService, Task> action)
    {
        var clear = true;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredKeyedService<ICalendarService>(InnerServiceKey);
            await action(inner);
            clear = false;
        }
        finally
        {
            if (clear) Clear();
        }
    }
}
