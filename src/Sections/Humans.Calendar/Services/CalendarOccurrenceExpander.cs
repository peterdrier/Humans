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
    /// <summary>
    /// Fallback zone for pure expansion callers; window reads always supply the viewer's zone.
    /// </summary>
    private static readonly DateTimeZone OrganisationZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public static IReadOnlyList<CalendarOccurrence> Expand(
        IReadOnlyList<CalendarEventInfo> events, Instant from, Instant to,
        IReadOnlyDictionary<Guid, string> teamNamesById, ILogger logger, DateTimeZone? viewerZone = null)
    {
        var displayZone = viewerZone ?? OrganisationZone;
        var results = new List<CalendarOccurrence>();
        // Dates themselves never convert; only the window's bounds do.
        var fromDate = from.InZone(displayZone).Date;
        var toLocal = to.InZone(displayZone).LocalDateTime;
        var toDate = toLocal.TimeOfDay == LocalTime.Midnight ? toLocal.Date : toLocal.Date.PlusDays(1);
        foreach (var ev in events)
        {
            var name = teamNamesById.GetValueOrDefault(ev.OwningTeamId, string.Empty);
            var recurring = !string.IsNullOrWhiteSpace(ev.RecurrenceRule);
            var occurrences = BuildOccurrences(ev, name, recurring, from, to, fromDate, toDate, logger);
            if (occurrences is null) continue;

            results.AddRange(ApplyExceptions(ev, name, recurring, occurrences)
                .Where(result => OverlapsWindow(result, from, to, fromDate, toDate)));
        }
        return OrderForDisplay(results, displayZone);
    }

    private static IEnumerable<CalendarOccurrence> ApplyExceptions(
        CalendarEventInfo ev, string name, bool recurring, IEnumerable<CalendarOccurrence> occurrences)
    {
        var handled = new HashSet<Guid>();
        foreach (var occurrence in occurrences)
        {
            var exception = recurring ? ev.Exceptions.FirstOrDefault(x => ev.IsAllDay
                ? x.OriginalOccurrenceDate == occurrence.OriginalOccurrenceDate
                : x.OriginalOccurrenceStartUtc == occurrence.OriginalOccurrenceStartUtc) : null;
            if (exception is not null) handled.Add(exception.Id);
            if (exception?.IsCancelled == true) continue;
            var result = exception is null ? occurrence : ApplyOverride(occurrence, exception);
            yield return result;
        }
        // Only an actual start move is independent of the current recurrence rule.
        // Text and end-only edits require an identity generated above, including extended lookback.
        foreach (var exception in ev.Exceptions.Where(x => !handled.Contains(x.Id) && !x.IsCancelled))
        {
            if (!recurring || (ev.IsAllDay
                ? exception.OverrideStartDate is null || exception.OverrideStartDate == exception.OriginalOccurrenceDate
                : exception.OverrideStartUtc is null || exception.OverrideStartUtc == exception.OriginalOccurrenceStartUtc))
                continue;
            var date = exception.OriginalOccurrenceDate;
            var start = exception.OriginalOccurrenceStartUtc;
            var original = ev.IsAllDay
                ? CreateOccurrence(ev, name, null, null, date,
                    date!.Value.PlusDays(NodaTime.Period.Between(ev.StartDate!.Value, ev.EndDateExclusive!.Value, PeriodUnits.Days).Days), true)
                : CreateOccurrence(ev, name, start,
                    ev.EndUtc is null ? null : start!.Value.Plus(ev.EndUtc.Value - ev.StartUtc!.Value), null, null, true);
            var result = ApplyOverride(original, exception);
            yield return result;
        }
    }

    /// <summary>
    /// Orders calendar-owned and contributed occurrences by the same viewer-local date.
    /// Contributed items are timed, while all-day Calendar items deliberately have no instant.
    /// </summary>
    internal static IReadOnlyList<CalendarOccurrence> OrderForDisplay(
        IEnumerable<CalendarOccurrence> occurrences, DateTimeZone viewerZone) => occurrences
        .OrderBy(o => o.StartDate ?? o.OccurrenceStartUtc!.Value.InZone(viewerZone).Date)
        .ThenBy(o => o.OccurrenceStartUtc)
        .ToList();

    private static List<CalendarOccurrence>? BuildOccurrences(
        CalendarEventInfo ev, string name, bool recurring, Instant from, Instant to,
        LocalDate fromDate, LocalDate toDate, ILogger logger)
    {
        if (!recurring)
        {
            return [CreateOccurrence(ev, name, ev.StartUtc, ev.EndUtc,
                ev.StartDate, ev.EndDateExclusive, recurring: false)];
        }

        return ev.IsAllDay
            ? ExpandAllDayOccurrences(ev, name, fromDate, toDate)
            : ExpandTimedOccurrences(ev, name, from, to, logger);
    }

    private static List<CalendarOccurrence> ExpandAllDayOccurrences(
        CalendarEventInfo ev, string name, LocalDate fromDate, LocalDate toDate)
    {
        var days = NodaTime.Period.Between(ev.StartDate!.Value, ev.EndDateExclusive!.Value, PeriodUnits.Days).Days;
        var ical = new IcalEvent
        {
            DtStart = new CalDateTime(ev.StartDate.Value.ToDateTimeUnspecified(), hasTime: false),
            DtEnd = new CalDateTime(ev.EndDateExclusive.Value.ToDateTimeUnspecified(), hasTime: false),
            RecurrenceRule = new RecurrencePattern(ev.RecurrenceRule!),
        };
        // End-only extensions can overlap this window from an earlier occurrence,
        // but their original identities must still be generated by the current rule.
        var searchDate = ev.Exceptions
            .Where(x => !x.IsCancelled && x.OverrideEndDateExclusive > fromDate
                && (x.OverrideStartDate is null || x.OverrideStartDate == x.OriginalOccurrenceDate))
            .Select(x => x.OriginalOccurrenceDate!.Value)
            .Append(fromDate.PlusDays(-days))
            .Min();
        searchDate = LocalDate.Max(searchDate, ev.StartDate.Value);
        var searchStart = new CalDateTime(searchDate.ToDateTimeUnspecified(), hasTime: false);
        return ical.GetOccurrences(searchStart, new EvaluationOptions())
            .TakeWhile(o => LocalDate.FromDateTime(o.Period.StartTime.Value) < toDate)
            .Select(item =>
            {
                var date = LocalDate.FromDateTime(item.Period.StartTime.Value);
                return CreateOccurrence(ev, name, null, null, date, date.PlusDays(days), recurring: true);
            })
            .ToList();
    }

    private static List<CalendarOccurrence>? ExpandTimedOccurrences(
        CalendarEventInfo ev, string name, Instant from, Instant to, ILogger logger)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(ev.RecurrenceTimezone!);
        if (zone is null)
        {
            logger.LogWarning("CalendarEvent {Id} has unknown timezone {Tz}; skipping occurrence expansion", ev.Id, ev.RecurrenceTimezone);
            return null;
        }

        var duration = (ev.EndUtc ?? ev.StartUtc!.Value) - ev.StartUtc!.Value;
        var ical = new IcalEvent
        {
            DtStart = new CalDateTime(ev.StartUtc.Value.InZone(zone).LocalDateTime.ToDateTimeUnspecified(), zone.Id, hasTime: true),
            Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(TimeSpan.FromTicks(duration.BclCompatibleTicks)),
            RecurrenceRule = new RecurrencePattern(ev.RecurrenceRule!),
        };
        var searchFrom = ev.Exceptions
            .Where(x => !x.IsCancelled && x.OverrideEndUtc > from
                && (x.OverrideStartUtc is null || x.OverrideStartUtc == x.OriginalOccurrenceStartUtc))
            .Select(x => x.OriginalOccurrenceStartUtc!.Value)
            .Append(from.Minus(duration))
            .Min();
        searchFrom = Instant.Max(searchFrom, ev.StartUtc.Value);
        var searchStart = new CalDateTime(searchFrom.InZone(zone).LocalDateTime.ToDateTimeUnspecified(), zone.Id, hasTime: true);
        // A repeated local hour can precede the instant cutoff even when its clock time is later.
        // Generate through a conservative local bound; OverlapsWindow checks the actual instants.
        var searchEnd = to.WithOffset(zone.MaxOffset).LocalDateTime.ToDateTimeUnspecified();
        return ical.GetOccurrences(searchStart, new EvaluationOptions())
            .TakeWhile(o => o.Period.StartTime.Value < searchEnd)
            .Select(item =>
            {
                var start = LocalDateTime.FromDateTime(item.Period.StartTime.Value).InZoneLeniently(zone).ToInstant();
                return CreateOccurrence(ev, name, start,
                    ev.EndUtc is null ? null : start.Plus(duration), null, null, recurring: true);
            })
            .ToList();
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
            : o.OccurrenceStartUtc < to &&
                (o.OccurrenceEndUtc is null || o.OccurrenceEndUtc == o.OccurrenceStartUtc
                    ? o.OccurrenceStartUtc >= from
                    : o.OccurrenceEndUtc > from);

    /// <summary>Conservative prefilter; exceptions may move occurrences beyond either series boundary.</summary>
    public static List<CalendarEventInfo> FilterForWindow(IEnumerable<CalendarEventInfo> snapshot,
        Instant from, Instant to, Guid? teamId, DateTimeZone? viewerZone = null)
    {
        var displayZone = viewerZone ?? OrganisationZone;
        return snapshot.Where(e =>
        {
            if (teamId is not null && e.OwningTeamId != teamId) return false;
            if (e.Exceptions.Count > 0) return true;
            if (e.IsAllDay)
                return e.StartDate <= to.InZone(displayZone).Date &&
                    (e.RecurrenceUntilDate is null || e.RecurrenceUntilDate >= from.InZone(displayZone).Date);

            // UNTIL bounds occurrence starts, while COUNT stores the final end.
            // Allow duration conservatively; expansion applies the exact overlap check.
            var duration = (e.EndUtc ?? e.StartUtc!.Value) - e.StartUtc!.Value;
            return e.StartUtc <= to &&
                (e.RecurrenceUntilUtc is null || e.RecurrenceUntilUtc.Value.Plus(duration) >= from);
        }).ToList();
    }

    // Older date events may have a DATE-TIME UNTIL. Interpret it in their original zone once.
    private static string? DateRule(string? rule, DateTimeZone zone) => rule is null ? null :
        string.Join(';', rule.Split(';').Select(part =>
        {
            if (!part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase) || part.Length <= 14) return part;
            var value = part[6..].ToUpperInvariant();
            var local = DateFormattingExtensions.IcalBasicDateTimePattern.Parse(value.TrimEnd('Z')).Value;
            var date = value.EndsWith('Z') ? local.InUtc().ToInstant().InZone(zone).Date : local.Date;
            return "UNTIL=" + DateFormattingExtensions.IcalBasicDatePattern.Format(date);
        }));

    /// <summary>Maps domain <c>CalendarEvent</c> (with Exceptions) to the immutable projection.</summary>
    public static CalendarEventInfo ToInfo(CalendarEvent ev)
    {
        // Legacy all-day instants are read as dates once, at the service boundary.
        // New writes use only the date columns; the old columns remain for existing rows.
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(ev.RecurrenceTimezone ?? OrganisationZone.Id)
            ?? OrganisationZone;
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
