using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Humans.Calendar.Contracts;

/// <summary>
/// Calendar's RRULE handling for sections that store their own repeating timed items, so every section repeats
/// a rule the same way the community calendar does.
/// </summary>
public static class CalendarRecurrence
{
    /// <summary>True for a blank rule or one Calendar would accept on a timed event.</summary>
    public static bool IsValidRule(string? rule) => CalendarRecurrenceRules.RecurrenceRuleRefusal(rule) is null;

    /// <summary>
    /// The occurrences of a timed item overlapping [<paramref name="from"/>, <paramref name="to"/>), first-occurrence
    /// <paramref name="start"/>..<paramref name="end"/> repeated by <paramref name="rule"/> in <paramref name="timeZoneId"/>.
    /// A blank rule yields the one item; an unknown zone yields nothing.
    /// </summary>
    public static IReadOnlyList<Interval> Occurrences(
        Instant start, Instant end, string? rule, string? timeZoneId, Instant from, Instant to)
    {
        var recurring = !string.IsNullOrWhiteSpace(rule);
        var info = new CalendarEventInfo(
            Id: Guid.Empty, Title: string.Empty, Description: null, Location: null, LocationUrl: null,
            OwningTeamId: Guid.Empty, StartUtc: start, EndUtc: end, IsAllDay: false,
            RecurrenceRule: recurring ? rule : null, RecurrenceTimezone: recurring ? timeZoneId : null,
            RecurrenceUntilUtc: null, CreatedAt: start, UpdatedAt: start,
            Exceptions: []);
        return CalendarOccurrenceExpander
            .Expand([info], from, to, new Dictionary<Guid, string>(), NullLogger.Instance)
            .Select(o => new Interval(o.OccurrenceStartUtc!.Value, o.OccurrenceEndUtc!.Value))
            .ToList();
    }
}
