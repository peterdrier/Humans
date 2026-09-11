using Humans.Calendar.Services.Dtos;
using Humans.Base.Caching;
using Humans.Calendar.Contracts;
using Humans.Teams.Contracts;
using Humans.Calendar.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Calendar.Services;

/// <summary>
/// Singleton cache-backed calendar read service. Holds every non-soft-deleted
/// <c>CalendarEventInfo</c> row with embedded exceptions, keyed by event id.
/// Write methods delegate to the keyed inner service and refresh the read cache.
/// </summary>
internal sealed class CachingCalendarService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingCalendarService> logger)
    : TrackedCache<Guid, CalendarEventInfo>("Calendar.Event", warmOnStartup: true, logger),
        ICalendarServiceRead, ICalendarService
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
        var occurrences = CalendarOccurrenceExpander.Expand(matched, from, to, teamNames, logger).ToList();

        // Community items have no team of their own (design §8) — only merge them into the
        // unfiltered, all-teams window. A ?teamId filter has nothing of theirs to show.
        if (teamId is null)
            occurrences.AddRange(await FanOutContributorItemsAsync(from, to, ct));

        return occurrences.OrderBy(o => o.OccurrenceStartUtc).ToList();
    }

    /// <summary>
    /// Fans out over every registered <see cref="ICalendarFeedContributor"/> for its public
    /// window items. Never cached — recomputed on every call, same as the team-name stitch
    /// in <see cref="ResolveTeamNamesAsync"/>, since only the underlying event dict is a
    /// tracked cache row. A throwing contributor is logged and skipped, not allowed to take
    /// down the rest of the community calendar.
    /// </summary>
    private async Task<IReadOnlyList<CalendarOccurrence>> FanOutContributorItemsAsync(
        Instant from, Instant to, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var contributors = scope.ServiceProvider.GetServices<ICalendarFeedContributor>();

        var items = new List<CalendarOccurrence>();
        foreach (var contributor in contributors)
        {
            IReadOnlyList<CalendarFeedItem> contributed;
            try
            {
                contributed = await contributor.GetPublicItemsForWindowAsync(from, to, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Community calendar contributor {Contributor} failed for window [{From}, {To}); its items are skipped",
                    contributor.GetType().Name, from, to);
                continue;
            }

            items.AddRange(contributed.Select(ToOccurrence));
        }

        return items;
    }

    private static CalendarOccurrence ToOccurrence(CalendarFeedItem item) => new(
        EventId: Guid.Empty,
        OccurrenceStartUtc: item.Start,
        OccurrenceEndUtc: item.End,
        IsAllDay: false,
        Title: item.Summary,
        Description: item.Description,
        Location: item.Location,
        LocationUrl: null,
        OwningTeamId: Guid.Empty,
        OwningTeamName: string.Empty,
        IsRecurring: false,
        OriginalOccurrenceStartUtc: null,
        Source: item.Source,
        Url: item.Url);

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

    public async Task<CalendarEventMutationResult> CreateEventWithResultAsync(
        CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        var result = await WithInner(inner => inner.CreateEventWithResultAsync(dto, createdByUserId, ct));
        if (result.Succeeded && result.Event is not null)
            await ReplaceAsync(result.Event.Id, ct);
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
