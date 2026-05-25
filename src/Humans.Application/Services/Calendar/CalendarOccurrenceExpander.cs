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
        var results = new List<CalendarOccurrence>();

        foreach (var e in events)
        {
            var owningTeamName = teamNamesById.TryGetValue(e.OwningTeamId, out var name)
                ? name
                : string.Empty;

            if (string.IsNullOrWhiteSpace(e.RecurrenceRule))
            {
                // Half-open [from, to): overlap when end > from AND start < to.
                var end = e.EndUtc ?? e.StartUtc;
                if (end <= from || e.StartUtc >= to) continue;
                results.Add(new CalendarOccurrence(
                    EventId: e.Id,
                    OccurrenceStartUtc: e.StartUtc,
                    OccurrenceEndUtc: e.EndUtc,
                    IsAllDay: e.IsAllDay,
                    Title: e.Title,
                    Description: e.Description,
                    Location: e.Location,
                    LocationUrl: e.LocationUrl,
                    OwningTeamId: e.OwningTeamId,
                    OwningTeamName: owningTeamName,
                    IsRecurring: false,
                    OriginalOccurrenceStartUtc: null));
            }
            else
            {
                var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(e.RecurrenceTimezone!);
                if (zone is null)
                {
                    logger.LogWarning(
                        "CalendarEvent {Id} has unknown timezone {Tz}; skipping occurrence expansion",
                        e.Id, e.RecurrenceTimezone);
                    continue;
                }

                var dur = (e.EndUtc ?? e.StartUtc) - e.StartUtc;
                var dtStartLocal = e.StartUtc.InZone(zone).LocalDateTime.ToDateTimeUnspecified();

                var icalEv = new IcalEvent
                {
                    DtStart = new CalDateTime(dtStartLocal, e.RecurrenceTimezone, hasTime: true),
                    Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(TimeSpan.FromTicks(dur.BclCompatibleTicks)),
                };
                icalEv.RecurrenceRule = new RecurrencePattern(e.RecurrenceRule!);

                var fromLocal = from.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
                var toLocal = to.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
                var fromCalDt = new CalDateTime(fromLocal, e.RecurrenceTimezone, hasTime: true);

                foreach (var iocc in icalEv.GetOccurrences(fromCalDt, new EvaluationOptions())
                    .TakeWhile(o => o.Period.StartTime.Value < toLocal))
                {
                    var occLocal = LocalDateTime.FromDateTime(iocc.Period.StartTime.Value);
                    var startInstant = occLocal.InZoneLeniently(zone).ToInstant();
                    var endInstant = e.EndUtc is null
                        ? (Instant?)null
                        : startInstant.Plus(e.EndUtc.Value - e.StartUtc);

                    results.Add(new CalendarOccurrence(
                        EventId: e.Id,
                        OccurrenceStartUtc: startInstant,
                        OccurrenceEndUtc: endInstant,
                        IsAllDay: e.IsAllDay,
                        Title: e.Title,
                        Description: e.Description,
                        Location: e.Location,
                        LocationUrl: e.LocationUrl,
                        OwningTeamId: e.OwningTeamId,
                        OwningTeamName: owningTeamName,
                        IsRecurring: true,
                        OriginalOccurrenceStartUtc: startInstant));
                }
            }
        }

        // Build a per-event exception lookup once.
        var exceptionsByEvent = events
            .ToDictionary(e => e.Id, e =>
                e.Exceptions.ToDictionary(x => x.OriginalOccurrenceStartUtc));

        var finalResults = new List<CalendarOccurrence>();
        var handledExceptionKeys = new HashSet<(Guid EventId, Instant OriginalStart)>();

        foreach (var occ in results)
        {
            if (!occ.IsRecurring || occ.OriginalOccurrenceStartUtc is null)
            {
                finalResults.Add(occ);
                continue;
            }

            if (!exceptionsByEvent.TryGetValue(occ.EventId, out var perEvent) ||
                !perEvent.TryGetValue(occ.OriginalOccurrenceStartUtc.Value, out var ex))
            {
                finalResults.Add(occ);
                continue;
            }

            handledExceptionKeys.Add((occ.EventId, ex.OriginalOccurrenceStartUtc));

            if (ex.IsCancelled) continue; // drop

            // Apply overrides; drop if override moves it outside [from, to).
            var newStart = ex.OverrideStartUtc ?? occ.OccurrenceStartUtc;
            var newEnd = ex.OverrideEndUtc ?? occ.OccurrenceEndUtc;
            if (newStart >= to || (newEnd ?? newStart) <= from) continue;

            finalResults.Add(occ with
            {
                OccurrenceStartUtc = newStart,
                OccurrenceEndUtc = newEnd,
                Title = ex.OverrideTitle ?? occ.Title,
                Description = ex.OverrideDescription ?? occ.Description,
                Location = ex.OverrideLocation ?? occ.Location,
                LocationUrl = ex.OverrideLocationUrl ?? occ.LocationUrl,
            });
        }

        // Inject overrides that moved INTO the window from an out-of-window original (expansion missed them).
        foreach (var ev in events)
        {
            var owningTeamName = teamNamesById.TryGetValue(ev.OwningTeamId, out var name)
                ? name
                : string.Empty;

            foreach (var ex in ev.Exceptions)
            {
                if (handledExceptionKeys.Contains((ev.Id, ex.OriginalOccurrenceStartUtc))) continue;
                if (ex.IsCancelled) continue;
                if (ex.OverrideStartUtc is null) continue;

                var newStart = ex.OverrideStartUtc.Value;
                var eventDuration = (ev.EndUtc ?? ev.StartUtc) - ev.StartUtc;
                var newEnd = ex.OverrideEndUtc
                    ?? (ev.EndUtc is null ? null : newStart.Plus(eventDuration));

                if (newStart >= to || (newEnd ?? newStart) <= from) continue;

                finalResults.Add(new CalendarOccurrence(
                    EventId: ev.Id,
                    OccurrenceStartUtc: newStart,
                    OccurrenceEndUtc: newEnd,
                    IsAllDay: ev.IsAllDay,
                    Title: ex.OverrideTitle ?? ev.Title,
                    Description: ex.OverrideDescription ?? ev.Description,
                    Location: ex.OverrideLocation ?? ev.Location,
                    LocationUrl: ex.OverrideLocationUrl ?? ev.LocationUrl,
                    OwningTeamId: ev.OwningTeamId,
                    OwningTeamName: owningTeamName,
                    IsRecurring: true,
                    OriginalOccurrenceStartUtc: ex.OriginalOccurrenceStartUtc));
            }
        }

        return finalResults.OrderBy(o => o.OccurrenceStartUtc).ToList();
    }

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
