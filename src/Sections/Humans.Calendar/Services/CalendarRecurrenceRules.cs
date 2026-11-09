using Humans.Calendar.Services.Dtos;
using Humans.Base.Extensions;
using Ical.Net;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Calendar.Services;

internal static class CalendarRecurrenceRules
{
    // Reject malformed RRULE at write time so reads can't crash during expansion.
    internal static string? RecurrenceRuleRefusal(string? rrule)
    {
        if (rrule is { Length: > 500 })
            return "Recurrence rules cannot exceed 500 characters.";
        if (string.IsNullOrWhiteSpace(rrule)) return null;
        try
        {
            var pattern = ParseRecurrenceRule(rrule);
            var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var names = parts.Select(part => part[..part.IndexOf('=')].Trim()).ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                return "Recurrence rule is malformed: rule parts cannot repeat.";
            // The library silently ignores UNTIL values it cannot parse.
            if (names.Contains("UNTIL", StringComparer.OrdinalIgnoreCase) && pattern.Until is null)
                return "Recurrence rule is malformed: UNTIL must be a valid DATE or DATE-TIME.";
            // The library accepts numeric enum values, which also bypass the all-day frequency guard.
            if (parts.Any(part => part.StartsWith("FREQ=", StringComparison.OrdinalIgnoreCase) &&
                    part[5..].Trim().ToUpperInvariant() is not ("SECONDLY" or "MINUTELY" or "HOURLY" or
                        "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY")))
                return "Recurrence rule is malformed: FREQ must name an RFC 5545 frequency.";
            if (pattern.ByDay.Any(day => day.Offset is < -53 or 0 or > 53))
                return "Recurrence rule is malformed: BYDAY ordinal offsets must be between -53 and 53, excluding zero.";
            // The parser accepts these combinations, but RFC 5545 section 3.3.10 forbids them.
            if (pattern.ByDay.Any(day => day.Offset is not null) &&
                (pattern.Frequency is not (FrequencyType.Monthly or FrequencyType.Yearly) || pattern.ByWeekNo.Count > 0))
                return "Recurrence rule is malformed: ordinal BYDAY requires MONTHLY or YEARLY without BYWEEKNO.";
            if (pattern.ByMonthDay.Count > 0 && pattern.Frequency == FrequencyType.Weekly)
                return "Recurrence rule is malformed: BYMONTHDAY cannot be used with WEEKLY.";
            if (pattern.ByYearDay.Count > 0 &&
                pattern.Frequency is FrequencyType.Daily or FrequencyType.Weekly or FrequencyType.Monthly)
                return "Recurrence rule is malformed: BYYEARDAY cannot be used with DAILY, WEEKLY or MONTHLY.";
            if (pattern.ByWeekNo.Count > 0 && pattern.Frequency != FrequencyType.Yearly)
                return "Recurrence rule is malformed: BYWEEKNO requires YEARLY.";
            if (pattern.BySetPosition.Count > 0 && !names.Any(name =>
                    name.StartsWith("BY", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "BYSETPOS", StringComparison.OrdinalIgnoreCase)))
                return "Recurrence rule is malformed: BYSETPOS requires another BY rule part.";
        }
        catch (Exception ex)
        {
            return $"Recurrence rule is malformed: {ex.Message}";
        }
        return null;
    }

    // At write time too: the controller guard doesn't cover jobs and tests.
    internal static string? TimezoneRefusal(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return null;
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz) is null)
            return $"Recurrence timezone is unknown: '{tz}'.";
        return null;
    }

    // Read by CalendarOccurrenceExpander.FilterForWindow's prefilter. Null only for open-ended rules.
    internal static Instant? ComputeRecurrenceUntilUtc(string? rrule, string? tz, Instant? dtStart, Instant? dtEnd)
    {
        if (dtStart is null || string.IsNullOrWhiteSpace(rrule) || string.IsNullOrWhiteSpace(tz)) return null;

        int? count = null;
        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var val = part[(eq + 1)..].Trim();

            if (string.Equals(key, "UNTIL", StringComparison.OrdinalIgnoreCase))
            {
                // Match the recurrence library's case-insensitive DATE-TIME parsing.
                val = val.ToUpperInvariant();
                var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);
                if (zone is null) return null;

                if (val.EndsWith('Z'))
                {
                    return DateFormattingExtensions.IcalBasicDateTimePattern.Parse(val[..^1]).Value
                        .InUtc().ToInstant();
                }
                if (val.Contains('T'))
                {
                    var local = DateFormattingExtensions.IcalBasicDateTimePattern.Parse(val).Value;
                    return local.InZoneLeniently(zone).ToInstant();
                }
                // DATE form — treat UNTIL as end-of-day in the rule's timezone.
                var date = DateFormattingExtensions.IcalBasicDatePattern.Parse(val).Value;
                return date.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();
            }
            else if (string.Equals(key, "COUNT", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(val, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var c) && c > 0)
                {
                    count = c;
                }
            }
        }

        if (count is null) return null;

        var ruleZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);
        if (ruleZone is null) return null;

        var dtStartLocal = dtStart.Value.InZone(ruleZone).LocalDateTime.ToDateTimeUnspecified();
        var duration = (dtEnd ?? dtStart.Value) - dtStart.Value;

        var icalEv = new IcalEvent
        {
            DtStart = new CalDateTime(dtStartLocal, tz, hasTime: true),
            Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(TimeSpan.FromTicks(duration.BclCompatibleTicks)),
        };
        icalEv.RecurrenceRule = ParseRecurrenceRule(rrule);

        var startCalDt = new CalDateTime(dtStartLocal, tz, hasTime: true);
        var last = icalEv.GetOccurrences(startCalDt, new EvaluationOptions())
            .Take(count.Value)
            .LastOrDefault();
        if (last is null) return null;

        var lastLocal = LocalDateTime.FromDateTime(last.Period.StartTime.Value);
        var lastStart = lastLocal.InZoneLeniently(ruleZone).ToInstant();
        return lastStart.Plus(duration);
    }

    internal static LocalDate? ComputeRecurrenceUntilDate(string? rule, LocalDate? start, LocalDate? end)
    {
        if (string.IsNullOrWhiteSpace(rule) || start is null || end is null) return null;
        var pattern = ParseRecurrenceRule(rule);
        var days = NodaTime.Period.Between(start.Value, end.Value, PeriodUnits.Days).Days;
        if (pattern.Until is not null)
            return LocalDate.FromDateTime(pattern.Until.Value).PlusDays(days);
        if (pattern.Count is not > 0) return null;
        var ical = new IcalEvent
        {
            DtStart = new CalDateTime(start.Value.ToDateTimeUnspecified(), hasTime: false),
            RecurrenceRule = pattern,
        };
        var last = ical.GetOccurrences(ical.DtStart, new EvaluationOptions()).Take(pattern.Count.Value).LastOrDefault();
        return last is null ? null : LocalDate.FromDateTime(last.Period.StartTime.Value).PlusDays(days);
    }

    internal static bool IsDateOnlyRecurrence(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return true;
        var pattern = ParseRecurrenceRule(rule);
        return pattern.Frequency is not (FrequencyType.Hourly or FrequencyType.Minutely or FrequencyType.Secondly)
            && pattern.ByHour.Count == 0 && pattern.ByMinute.Count == 0 && pattern.BySecond.Count == 0
            && rule.Split(';').All(part => !part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase)
                || part[6..].Trim().Length == 8);
    }

    // Ical.Net accepts value whitespace but silently ignores a padded DATE UNTIL.
    internal static RecurrencePattern ParseRecurrenceRule(string rule) => new(
        string.Join(';', rule.Split(';').Select(part =>
        {
            var equals = part.IndexOf('=');
            return equals < 0 ? part : part[..(equals + 1)] + part[(equals + 1)..].Trim();
        })));

    // Older date events may have a DATE-TIME UNTIL. Interpret it in their original zone once.
    internal static string? DateRule(string? rule, DateTimeZone zone) => rule is null ? null :
        string.Join(';', rule.Split(';').Select(part =>
        {
            if (!part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase)) return part;
            var value = part[6..].Trim().ToUpperInvariant();
            if (!value.Contains('T')) return part;
            var local = DateFormattingExtensions.IcalBasicDateTimePattern.Parse(value.TrimEnd('Z')).Value;
            var date = value.EndsWith('Z') ? local.InUtc().ToInstant().InZone(zone).Date : local.Date;
            return "UNTIL=" + DateFormattingExtensions.IcalBasicDatePattern.Format(date);
        }));

    internal static string? GetOverrideRefusal(
        CalendarEventInfo info, Instant? originalUtc, LocalDate? originalDate, OverrideOccurrenceDto? dto)
    {
        if (dto is null) return null; // Cancellation preserves the existing override.
        if (info.IsAllDay)
        {
            if (dto.OverrideStartUtc is not null || dto.OverrideEndUtc is not null)
                return "An all-day occurrence cannot have a time.";
            var start = dto.OverrideStartDate ?? originalDate!.Value;
            LocalDate end;
            try
            {
                var days = NodaTime.Period.Between(info.StartDate!.Value, info.EndDateExclusive!.Value, PeriodUnits.Days).Days;
                // Expansion reconstructs the original range before applying a moved override.
                _ = originalDate!.Value.PlusDays(days);
                end = dto.OverrideEndDateExclusive ?? start.PlusDays(days);
            }
            catch (OverflowException)
            {
                return "An all-day occurrence requires a representable date range.";
            }
            if (end <= start) return "An all-day occurrence requires a non-empty date range.";
        }
        else
        {
            if (dto.OverrideStartDate is not null || dto.OverrideEndDateExclusive is not null)
                return "A timed occurrence cannot have all-day dates.";
            var start = dto.OverrideStartUtc ?? originalUtc!.Value;
            Instant end;
            try
            {
                var duration = (info.EndUtc ?? info.StartUtc!.Value) - info.StartUtc!.Value;
                _ = originalUtc!.Value.Plus(duration);
                end = dto.OverrideEndUtc ?? start.Plus(duration);
            }
            catch (OverflowException)
            {
                return "A timed occurrence requires a representable time range.";
            }
            if (end < start) return "An occurrence cannot end before it starts.";
        }
        return null;
    }

}
