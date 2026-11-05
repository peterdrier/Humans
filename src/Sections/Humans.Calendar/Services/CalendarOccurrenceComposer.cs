using Humans.Base.Interfaces;
using Humans.Calendar.Contracts;
using Humans.Calendar.Services.Dtos;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Calendar.Services;

internal sealed class CalendarOccurrenceComposer(
    ITeamService teams,
    IServiceScopeFactory scopeFactory,
    ILogger<CalendarService> logger) : IApplicationService
{
    public async Task<IReadOnlyList<CalendarOccurrence>> ComposeOccurrencesAsync(
        IReadOnlyList<CalendarEventInfo> events, Instant from, Instant to, DateTimeZone viewerZone,
        Guid? teamId = null, CancellationToken ct = default)
    {
        var matched = CalendarOccurrenceExpander.FilterForWindow(
            events,
            from, to, teamId, viewerZone);

        var teamNames = await ResolveTeamNamesAsync(matched, ct);
        var occurrences = CalendarOccurrenceExpander.Expand(matched, from, to, teamNames, logger, viewerZone).ToList();

        // Community items carry no team, so a ?teamId filter has nothing of theirs to show.
        if (teamId is null)
            occurrences.AddRange(await FanOutContributorItemsAsync(from, to, ct));

        return occurrences;
    }

    /// <summary>
    /// Never cached. A throwing contributor is logged and skipped so it can't take down the community calendar.
    /// </summary>
    private async Task<IReadOnlyList<CalendarOccurrence>> FanOutContributorItemsAsync(
        Instant from, Instant to, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var contributors = scope.ServiceProvider.GetServices<ICalendarFeedContributor>();

        var items = new List<CalendarOccurrence>();
        foreach (var contributor in contributors)
        {
            try
            {
                var contributed = await contributor.GetPublicItemsForWindowAsync(from, to, ct);
                var occurrences = contributed.SelectMany(item => ToOccurrences(item, from, to)).ToList();
                items.AddRange(occurrences);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Community calendar contributor {Contributor} failed for window [{From}, {To}); its items are skipped",
                    contributor.GetType().Name, from, to);
                continue;
            }

        }

        return items;
    }

    private static IEnumerable<CalendarOccurrence> ToOccurrences(CalendarFeedItem item, Instant from, Instant to) =>
        item.RecurrenceRule is null
            ? [ToOccurrence(item, item.Start, item.End, recurring: false)]
            : CalendarRecurrence.Occurrences(item.Start, item.End, item.RecurrenceRule, item.RecurrenceTimezone, from, to)
                .Select(o => ToOccurrence(item, o.Start, o.End, recurring: true));

    private static CalendarOccurrence ToOccurrence(CalendarFeedItem item, Instant start, Instant end, bool recurring) => new(
        EventId: Guid.Empty,
        OccurrenceStartUtc: start,
        OccurrenceEndUtc: end,
        IsAllDay: false,
        Title: item.Summary,
        Description: item.Description,
        Location: item.Location,
        LocationUrl: item.LocationUrl,
        OwningTeamId: Guid.Empty,
        OwningTeamName: string.Empty,
        IsRecurring: recurring,
        OriginalOccurrenceStartUtc: recurring ? start : null,
        Source: item.Source,
        Url: item.Url);

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveTeamNamesAsync(
        IReadOnlyList<CalendarEventInfo> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return new Dictionary<Guid, string>();

        var teamIds = events.Select(e => e.OwningTeamId).Distinct().ToList();
        var teamsById = await teams.GetTeamsAsync(ct);
        return teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
    }
}
