using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.Calendar;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.Extensions.Logging;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Application.Services.Calendar;

/// <summary>
/// Pure expansion over prefiltered <see cref="CalendarEventInfo"/> rows for window <c>[from, to)</c>.
/// Returns sorted <see cref="CalendarOccurrence"/> list with recurrence expansion + exception merge.
/// No I/O — callable from the §15 caching decorator over cached projections.
/// </summary>
public static class CalendarOccurrenceExpander
{
    public static IReadOnlyList<CalendarOccurrence> Expand(
        IReadOnlyList<CalendarEventInfo> events,
        Instant from,
        Instant to,
        IReadOnlyDictionary<Guid, string> teamNamesById,
        ILogger logger)
    {
        var expanded = new List<CalendarOccurrence>();

        foreach (var e in events)
        {
            var owningTeamName = ResolveTeamName(teamNamesById, e.OwningTeamId);

            if (string.IsNullOrWhiteSpace(e.RecurrenceRule))
                AddSingleOccurrence(expanded, e, owningTeamName, from, to);
            else
                AddRecurringOccurrences(expanded, e, owningTeamName, from, to, logger);
        }

        var exceptionsByEvent = events
            .ToDictionary(e => e.Id, e =>
                e.Exceptions.ToDictionary(x => x.OriginalOccurrenceStartUtc));

        var finalResults = ApplyExceptions(expanded, exceptionsByEvent, from, to, out var handledExceptionKeys);
        AddMovedOverrides(finalResults, events, teamNamesById, handledExceptionKeys, from, to);

        return finalResults.OrderBy(o => o.OccurrenceStartUtc).ToList();
    }

    private static void AddSingleOccurrence(
        List<CalendarOccurrence> results,
        CalendarEventInfo e,
        string owningTeamName,
        Instant from,
        Instant to)
    {
        if (!OverlapsWindow(e.StartUtc, e.EndUtc, from, to)) return;

        results.Add(CreateOccurrence(
            e,
            owningTeamName,
            e.StartUtc,
            e.EndUtc,
            isRecurring: false,
            originalStart: null));
    }

    private static void AddRecurringOccurrences(
        List<CalendarOccurrence> results,
        CalendarEventInfo e,
        string owningTeamName,
        Instant from,
        Instant to,
        ILogger logger)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(e.RecurrenceTimezone!);
        if (zone is null)
        {
            logger.LogWarning(
                "CalendarEvent {Id} has unknown timezone {Tz}; skipping occurrence expansion",
                e.Id, e.RecurrenceTimezone);
            return;
        }

        var icalEv = CreateIcalEvent(e, zone);
        var toLocal = to.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
        var fromCalDt = new CalDateTime(
            from.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
            e.RecurrenceTimezone,
            hasTime: true);

        foreach (var iocc in icalEv.GetOccurrences(fromCalDt, new EvaluationOptions())
            .TakeWhile(o => o.Period.StartTime.Value < toLocal))
        {
            var startInstant = LocalDateTime
                .FromDateTime(iocc.Period.StartTime.Value)
                .InZoneLeniently(zone)
                .ToInstant();
            var endInstant = e.EndUtc is null
                ? (Instant?)null
                : startInstant.Plus(e.EndUtc.Value - e.StartUtc);

            results.Add(CreateOccurrence(
                e,
                owningTeamName,
                startInstant,
                endInstant,
                isRecurring: true,
                originalStart: startInstant));
        }
    }

    private static IcalEvent CreateIcalEvent(CalendarEventInfo e, DateTimeZone zone)
    {
        var duration = (e.EndUtc ?? e.StartUtc) - e.StartUtc;
        var dtStartLocal = e.StartUtc.InZone(zone).LocalDateTime.ToDateTimeUnspecified();

        var icalEv = new IcalEvent
        {
            DtStart = new CalDateTime(dtStartLocal, e.RecurrenceTimezone, hasTime: true),
            Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(
                TimeSpan.FromTicks(duration.BclCompatibleTicks)),
        };
        icalEv.RecurrenceRule = new RecurrencePattern(e.RecurrenceRule!);
        return icalEv;
    }

    private static List<CalendarOccurrence> ApplyExceptions(
        IReadOnlyList<CalendarOccurrence> expanded,
        IReadOnlyDictionary<Guid, Dictionary<Instant, CalendarEventExceptionInfo>> exceptionsByEvent,
        Instant from,
        Instant to,
        out HashSet<(Guid EventId, Instant OriginalStart)> handledExceptionKeys)
    {
        var finalResults = new List<CalendarOccurrence>();
        handledExceptionKeys = [];

        foreach (var occ in expanded)
        {
            var exception = FindException(occ, exceptionsByEvent);
            if (exception is null)
            {
                finalResults.Add(occ);
                continue;
            }

            handledExceptionKeys.Add((occ.EventId, exception.OriginalOccurrenceStartUtc));

            if (exception.IsCancelled) continue;

            var overridden = ApplyOverride(occ, exception);
            if (OverlapsWindow(overridden.OccurrenceStartUtc, overridden.OccurrenceEndUtc, from, to))
                finalResults.Add(overridden);
        }

        return finalResults;
    }

    private static CalendarEventExceptionInfo? FindException(
        CalendarOccurrence occ,
        IReadOnlyDictionary<Guid, Dictionary<Instant, CalendarEventExceptionInfo>> exceptionsByEvent)
    {
        if (!occ.IsRecurring || occ.OriginalOccurrenceStartUtc is null) return null;

        return exceptionsByEvent.TryGetValue(occ.EventId, out var perEvent) &&
            perEvent.TryGetValue(occ.OriginalOccurrenceStartUtc.Value, out var ex)
                ? ex
                : null;
    }

    private static void AddMovedOverrides(
        List<CalendarOccurrence> finalResults,
        IReadOnlyList<CalendarEventInfo> events,
        IReadOnlyDictionary<Guid, string> teamNamesById,
        HashSet<(Guid EventId, Instant OriginalStart)> handledExceptionKeys,
        Instant from,
        Instant to)
    {
        foreach (var ev in events)
        {
            var owningTeamName = ResolveTeamName(teamNamesById, ev.OwningTeamId);

            foreach (var ex in ev.Exceptions)
            {
                if (!ShouldInjectMovedOverride(ev.Id, ex, handledExceptionKeys)) continue;
                if (ex.OverrideStartUtc is not { } newStart) continue;

                var newEnd = ResolveOverrideEnd(ev, ex, newStart);

                if (!OverlapsWindow(newStart, newEnd, from, to)) continue;

                finalResults.Add(CreateOccurrence(
                    ev,
                    owningTeamName,
                    newStart,
                    newEnd,
                    isRecurring: true,
                    originalStart: ex.OriginalOccurrenceStartUtc,
                    title: ex.OverrideTitle,
                    description: ex.OverrideDescription,
                    location: ex.OverrideLocation,
                    locationUrl: ex.OverrideLocationUrl));
            }
        }
    }

    private static bool ShouldInjectMovedOverride(
        Guid eventId,
        CalendarEventExceptionInfo ex,
        HashSet<(Guid EventId, Instant OriginalStart)> handledExceptionKeys) =>
        !handledExceptionKeys.Contains((eventId, ex.OriginalOccurrenceStartUtc)) &&
        !ex.IsCancelled &&
        ex.OverrideStartUtc is not null;

    private static Instant? ResolveOverrideEnd(
        CalendarEventInfo ev,
        CalendarEventExceptionInfo ex,
        Instant newStart)
    {
        if (ex.OverrideEndUtc is { } overrideEnd) return overrideEnd;
        if (ev.EndUtc is null) return null;

        return newStart.Plus((ev.EndUtc.Value - ev.StartUtc));
    }

    private static CalendarOccurrence ApplyOverride(
        CalendarOccurrence occurrence,
        CalendarEventExceptionInfo ex) =>
        occurrence with
        {
            OccurrenceStartUtc = ex.OverrideStartUtc ?? occurrence.OccurrenceStartUtc,
            OccurrenceEndUtc = ex.OverrideEndUtc ?? occurrence.OccurrenceEndUtc,
            Title = ex.OverrideTitle ?? occurrence.Title,
            Description = ex.OverrideDescription ?? occurrence.Description,
            Location = ex.OverrideLocation ?? occurrence.Location,
            LocationUrl = ex.OverrideLocationUrl ?? occurrence.LocationUrl,
        };

    private static CalendarOccurrence CreateOccurrence(
        CalendarEventInfo ev,
        string owningTeamName,
        Instant start,
        Instant? end,
        bool isRecurring,
        Instant? originalStart,
        string? title = null,
        string? description = null,
        string? location = null,
        string? locationUrl = null) =>
        new(
            EventId: ev.Id,
            OccurrenceStartUtc: start,
            OccurrenceEndUtc: end,
            IsAllDay: ev.IsAllDay,
            Title: title ?? ev.Title,
            Description: description ?? ev.Description,
            Location: location ?? ev.Location,
            LocationUrl: locationUrl ?? ev.LocationUrl,
            OwningTeamId: ev.OwningTeamId,
            OwningTeamName: owningTeamName,
            IsRecurring: isRecurring,
            OriginalOccurrenceStartUtc: originalStart);

    private static bool OverlapsWindow(Instant start, Instant? end, Instant from, Instant to) =>
        start < to && (end ?? start) > from;

    private static string ResolveTeamName(IReadOnlyDictionary<Guid, string> teamNamesById, Guid teamId) =>
        teamNamesById.TryGetValue(teamId, out var name) ? name : string.Empty;

    /// <summary>Mirrors the SQL prefilter in <c>CalendarRepository.GetEventsInWindowAsync</c>.</summary>
    public static List<CalendarEventInfo> FilterForWindow(
        IEnumerable<CalendarEventInfo> snapshot,
        Instant from,
        Instant to,
        Guid? teamId)
    {
        var result = new List<CalendarEventInfo>();
        foreach (var e in snapshot)
        {
            if (e.StartUtc > to) continue;
            if (e.RecurrenceUntilUtc is { } until && until < from) continue;
            if (teamId is { } t && e.OwningTeamId != t) continue;
            result.Add(e);
        }
        return result;
    }

    /// <summary>Maps domain <c>CalendarEvent</c> (with Exceptions) to the immutable projection.</summary>
    public static CalendarEventInfo ToInfo(Domain.Entities.CalendarEvent ev) => new(
        Id: ev.Id,
        Title: ev.Title,
        Description: ev.Description,
        Location: ev.Location,
        LocationUrl: ev.LocationUrl,
        OwningTeamId: ev.OwningTeamId,
        StartUtc: ev.StartUtc,
        EndUtc: ev.EndUtc,
        IsAllDay: ev.IsAllDay,
        RecurrenceRule: ev.RecurrenceRule,
        RecurrenceTimezone: ev.RecurrenceTimezone,
        RecurrenceUntilUtc: ev.RecurrenceUntilUtc,
        CreatedByUserId: ev.CreatedByUserId,
        CreatedAt: ev.CreatedAt,
        UpdatedAt: ev.UpdatedAt,
        Exceptions: ev.Exceptions
            .Select(x => new CalendarEventExceptionInfo(
                Id: x.Id,
                OriginalOccurrenceStartUtc: x.OriginalOccurrenceStartUtc,
                IsCancelled: x.IsCancelled,
                OverrideStartUtc: x.OverrideStartUtc,
                OverrideEndUtc: x.OverrideEndUtc,
                OverrideTitle: x.OverrideTitle,
                OverrideDescription: x.OverrideDescription,
                OverrideLocation: x.OverrideLocation,
                OverrideLocationUrl: x.OverrideLocationUrl))
            .ToList());
}
