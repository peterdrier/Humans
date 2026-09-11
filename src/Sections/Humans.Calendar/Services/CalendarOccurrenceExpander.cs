using Humans.Calendar.Domain;
using Humans.Calendar.Services.Dtos;
using Humans.Base.Extensions;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Calendar.Services;

/// <summary>Pure date or instant recurrence expansion over the cached event projection.</summary>
internal static class CalendarOccurrenceExpander
{
    public static IReadOnlyList<CalendarOccurrence> Expand(
        IReadOnlyList<CalendarEventInfo> events, Instant from, Instant to,
        IReadOnlyDictionary<Guid, string> teamNamesById, ILogger logger)
    {
        var results = new List<CalendarOccurrence>();
        // Calendar currently uses the organisation's viewer zone. Dates themselves never convert.
        var viewerZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var fromDate = from.InZone(viewerZone).Date;
        var toLocal = to.InZone(viewerZone).LocalDateTime;
        var toDate = toLocal.TimeOfDay == LocalTime.Midnight ? toLocal.Date : toLocal.Date.PlusDays(1);
        foreach (var ev in events)
        {
            var name = teamNamesById.GetValueOrDefault(ev.OwningTeamId, string.Empty);
            var occurrences = new List<CalendarOccurrence>();
            var recurring = !string.IsNullOrWhiteSpace(ev.RecurrenceRule);
            if (!recurring)
                occurrences.Add(CreateOccurrence(ev, name, ev.StartUtc, ev.EndUtc, ev.StartDate, ev.EndDateExclusive, false));
            else if (ev.IsAllDay)
            {
                var days = NodaTime.Period.Between(ev.StartDate!.Value, ev.EndDateExclusive!.Value, PeriodUnits.Days).Days;
                var ical = new IcalEvent
                {
                    DtStart = new CalDateTime(ev.StartDate.Value.ToDateTimeUnspecified(), hasTime: false),
                    DtEnd = new CalDateTime(ev.EndDateExclusive.Value.ToDateTimeUnspecified(), hasTime: false),
                    RecurrenceRule = new RecurrencePattern(ev.RecurrenceRule!),
                };
                var searchStart = new CalDateTime(fromDate.PlusDays(-days).ToDateTimeUnspecified(), hasTime: false);
                foreach (var item in ical.GetOccurrences(searchStart, new EvaluationOptions())
                    .TakeWhile(o => LocalDate.FromDateTime(o.Period.StartTime.Value) < toDate))
                {
                    var date = LocalDate.FromDateTime(item.Period.StartTime.Value);
                    occurrences.Add(CreateOccurrence(ev, name, null, null, date, date.PlusDays(days), true));
                }
            }
            else
            {
                var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(ev.RecurrenceTimezone!);
                if (zone is null)
                {
                    logger.LogWarning("CalendarEvent {Id} has unknown timezone {Tz}; skipping occurrence expansion", ev.Id, ev.RecurrenceTimezone);
                    continue;
                }
                var duration = (ev.EndUtc ?? ev.StartUtc!.Value) - ev.StartUtc!.Value;
                var ical = new IcalEvent
                {
                    DtStart = new CalDateTime(ev.StartUtc.Value.InZone(zone).LocalDateTime.ToDateTimeUnspecified(), zone.Id, hasTime: true),
                    Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(TimeSpan.FromTicks(duration.BclCompatibleTicks)),
                    RecurrenceRule = new RecurrencePattern(ev.RecurrenceRule!),
                };
                var searchStart = new CalDateTime(from.Minus(duration).InZone(zone).LocalDateTime.ToDateTimeUnspecified(), zone.Id, hasTime: true);
                foreach (var item in ical.GetOccurrences(searchStart, new EvaluationOptions())
                    .TakeWhile(o => o.Period.StartTime.Value < to.InZone(zone).LocalDateTime.ToDateTimeUnspecified()))
                {
                    var start = LocalDateTime.FromDateTime(item.Period.StartTime.Value).InZoneLeniently(zone).ToInstant();
                    occurrences.Add(CreateOccurrence(ev, name, start, ev.EndUtc is null ? null : start.Plus(duration), null, null, true));
                }
            }

            var handled = new HashSet<Guid>();
            foreach (var occurrence in occurrences)
            {
                var exception = recurring ? ev.Exceptions.FirstOrDefault(x => ev.IsAllDay
                    ? x.OriginalOccurrenceDate == occurrence.OriginalOccurrenceDate
                    : x.OriginalOccurrenceStartUtc == occurrence.OriginalOccurrenceStartUtc) : null;
                if (exception is not null) handled.Add(exception.Id);
                if (exception?.IsCancelled == true) continue;
                var result = exception is null ? occurrence : ApplyOverride(occurrence, exception);
                if (OverlapsWindow(result, from, to, fromDate, toDate)) results.Add(result);
            }
            // A moved occurrence can be outside the series' original window in either direction.
            foreach (var exception in ev.Exceptions.Where(x => !handled.Contains(x.Id) && !x.IsCancelled))
            {
                if (!recurring) continue;
                var date = exception.OriginalOccurrenceDate;
                var start = exception.OriginalOccurrenceStartUtc;
                var original = ev.IsAllDay
                    ? CreateOccurrence(ev, name, null, null, date,
                        date!.Value.PlusDays(NodaTime.Period.Between(ev.StartDate!.Value, ev.EndDateExclusive!.Value, PeriodUnits.Days).Days), true)
                    : CreateOccurrence(ev, name, start,
                        ev.EndUtc is null ? null : start!.Value.Plus(ev.EndUtc.Value - ev.StartUtc!.Value), null, null, true);
                var result = ApplyOverride(original, exception);
                if (OverlapsWindow(result, from, to, fromDate, toDate)) results.Add(result);
            }
        }
        return results.OrderBy(o => o.StartDate ?? o.OccurrenceStartUtc!.Value.InZone(viewerZone).Date)
            .ThenBy(o => o.OccurrenceStartUtc).ToList();
    }

    private static CalendarOccurrence ApplyOverride(CalendarOccurrence occurrence, CalendarEventExceptionInfo ex)
    {
        var date = ex.OverrideStartDate ?? occurrence.StartDate;
        var start = ex.OverrideStartUtc ?? occurrence.OccurrenceStartUtc;
        return occurrence with
        {
            StartDate = date,
            EndDateExclusive = occurrence.IsAllDay ? ex.OverrideEndDateExclusive ?? date!.Value.PlusDays(
                NodaTime.Period.Between(occurrence.StartDate!.Value, occurrence.EndDateExclusive!.Value, PeriodUnits.Days).Days) : null,
            OccurrenceStartUtc = start,
            OccurrenceEndUtc = ex.OverrideEndUtc ?? (occurrence.OccurrenceEndUtc is { } end
                ? start!.Value.Plus(end - occurrence.OccurrenceStartUtc!.Value) : null),
            Title = ex.OverrideTitle ?? occurrence.Title,
            Description = ex.OverrideDescription ?? occurrence.Description,
            Location = ex.OverrideLocation ?? occurrence.Location,
            LocationUrl = ex.OverrideLocationUrl ?? occurrence.LocationUrl,
        };
    }

    private static CalendarOccurrence CreateOccurrence(CalendarEventInfo ev, string name,
        Instant? start, Instant? end, LocalDate? date, LocalDate? endDate, bool recurring) => new(
            ev.Id, start, end, ev.IsAllDay, ev.Title, ev.Description, ev.Location, ev.LocationUrl,
            ev.OwningTeamId, name, recurring, recurring ? start : null,
            date, endDate, recurring ? date : null);

    private static bool OverlapsWindow(CalendarOccurrence o, Instant from, Instant to, LocalDate fromDate, LocalDate toDate) =>
        o.IsAllDay ? o.StartDate < toDate && o.EndDateExclusive > fromDate
            : o.OccurrenceStartUtc < to && (o.OccurrenceEndUtc ?? o.OccurrenceStartUtc) > from;

    /// <summary>Conservative prefilter; exceptions may move occurrences beyond either series boundary.</summary>
    public static List<CalendarEventInfo> FilterForWindow(IEnumerable<CalendarEventInfo> snapshot,
        Instant from, Instant to, Guid? teamId)
    {
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        return snapshot.Where(e => (teamId is null || e.OwningTeamId == teamId) &&
            (e.Exceptions.Count > 0 || (e.IsAllDay
                ? e.StartDate <= to.InZone(zone).Date && (e.RecurrenceUntilDate is null || e.RecurrenceUntilDate >= from.InZone(zone).Date)
                : e.StartUtc <= to && (e.RecurrenceUntilUtc is null || e.RecurrenceUntilUtc >= from)))).ToList();
    }

    // Older date events may have a DATE-TIME UNTIL. Interpret it in their original zone once.
    private static string? DateRule(string? rule, DateTimeZone zone) => rule is null ? null :
        string.Join(';', rule.Split(';').Select(part =>
        {
            if (!part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase) || part.Length <= 14) return part;
            var value = part[6..];
            var local = DateFormattingExtensions.IcalBasicDateTimePattern.Parse(value.TrimEnd('Z')).Value;
            var date = value.EndsWith('Z') ? local.InUtc().ToInstant().InZone(zone).Date : local.Date;
            return "UNTIL=" + DateFormattingExtensions.IcalBasicDatePattern.Format(date);
        }));

    /// <summary>Maps domain <c>CalendarEvent</c> (with Exceptions) to the immutable projection.</summary>
    public static CalendarEventInfo ToInfo(CalendarEvent ev)
    {
        // Legacy all-day instants are read as dates once, at the service boundary.
        // New writes use only the date columns; the old columns remain for existing rows.
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(ev.RecurrenceTimezone ?? "Europe/Madrid")
            ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var startDate = ev.IsAllDay ? ev.StartDate ?? ev.StartUtc!.Value.InZone(zone).Date : (LocalDate?)null;
        var endDate = ev.IsAllDay ? ev.EndDateExclusive ??
            (ev.EndUtc is { } end ? end.Minus(NodaTime.Duration.FromNanoseconds(1)).InZone(zone).Date.PlusDays(1)
                : startDate!.Value.PlusDays(1)) : (LocalDate?)null;
        return new(
            Id: ev.Id,
            Title: ev.Title,
            Description: ev.Description,
            Location: ev.Location,
            LocationUrl: ev.LocationUrl,
            OwningTeamId: ev.OwningTeamId,
            StartUtc: ev.IsAllDay ? null : ev.StartUtc,
            EndUtc: ev.IsAllDay ? null : ev.EndUtc,
            IsAllDay: ev.IsAllDay,
            RecurrenceRule: ev.IsAllDay ? DateRule(ev.RecurrenceRule, zone) : ev.RecurrenceRule,
            RecurrenceTimezone: ev.RecurrenceTimezone,
            RecurrenceUntilUtc: ev.IsAllDay ? null : ev.RecurrenceUntilUtc,
            CreatedByUserId: ev.CreatedByUserId,
            CreatedAt: ev.CreatedAt,
            UpdatedAt: ev.UpdatedAt,
            Exceptions: ev.Exceptions
                .Select(x => new CalendarEventExceptionInfo(
                    Id: x.Id,
                    OriginalOccurrenceStartUtc: ev.IsAllDay ? null : x.OriginalOccurrenceStartUtc,
                    IsCancelled: x.IsCancelled,
                    OverrideStartUtc: ev.IsAllDay ? null : x.OverrideStartUtc,
                    OverrideEndUtc: ev.IsAllDay ? null : x.OverrideEndUtc,
                    OverrideTitle: x.OverrideTitle,
                    OverrideDescription: x.OverrideDescription,
                    OverrideLocation: x.OverrideLocation,
                    OverrideLocationUrl: x.OverrideLocationUrl,
                    OriginalOccurrenceDate: ev.IsAllDay ? x.OriginalOccurrenceDate ?? x.OriginalOccurrenceStartUtc!.Value.InZone(zone).Date : null,
                    OverrideStartDate: ev.IsAllDay ? x.OverrideStartDate ?? x.OverrideStartUtc?.InZone(zone).Date : null,
                    OverrideEndDateExclusive: ev.IsAllDay ? x.OverrideEndDateExclusive ??
                        x.OverrideEndUtc?.Minus(NodaTime.Duration.FromNanoseconds(1)).InZone(zone).Date.PlusDays(1) : null))
                .ToList(),
            StartDate: startDate,
            EndDateExclusive: endDate,
            RecurrenceUntilDate: ev.IsAllDay ? ev.RecurrenceUntilDate : null);
    }
}
