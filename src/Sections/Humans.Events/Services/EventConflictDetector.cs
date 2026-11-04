using NodaTime;
using Humans.Events.Services.Dtos;

namespace Humans.Events.Services;

/// <summary>The one owner of the overlap rule: half-open [start, start + duration) intersection.</summary>
internal static class EventConflictDetector
{
    public static bool Overlaps(
        Instant aStartAt, int aDurationMinutes,
        Instant bStartAt, int bDurationMinutes) =>
        aStartAt < bStartAt.Plus(Duration.FromMinutes(bDurationMinutes))
        && bStartAt < aStartAt.Plus(Duration.FromMinutes(aDurationMinutes));

    /// <summary>One warning per other camp event, dated at its earliest overlapping occurrence.</summary>
    public static IReadOnlyList<CampEventOverlap> FindCampDuplicateCandidates(
        EventInfo queued, IReadOnlyList<CampEventOverlap> candidates,
        LocalDate? gateOpeningDate, DateTimeZone? timeZone)
    {
        if (queued.CampId is null) return [];
        var queuedStarts = queued.GetOccurrenceInstants(gateOpeningDate, timeZone);
        var matches = new List<CampEventOverlap>();
        foreach (var other in candidates.Where(other => other.Id != queued.Id && other.CampId == queued.CampId))
        {
            Instant? firstOverlap = null;
            foreach (var start in EventRecurrenceDays.GetOccurrenceInstants(
                other.StartAt, other.IsRecurring, other.RecurrenceDays, gateOpeningDate, timeZone))
            {
                if (queuedStarts.Any(queuedStart => Overlaps(start, other.DurationMinutes, queuedStart, queued.DurationMinutes))
                    && (firstOverlap is null || start < firstOverlap.Value))
                    firstOverlap = start;
            }
            if (firstOverlap is { } matchedStart)
                matches.Add(other with { StartAt = matchedStart });
        }
        return matches;
    }

    /// <summary>O(n²): inputs are small (one user's schedule, one camp's events).</summary>
    public static IReadOnlySet<int> FindConflictingIndexes<T>(
        IReadOnlyList<T> items,
        Func<T, Instant> startAt,
        Func<T, int> durationMinutes)
    {
        var conflicted = new HashSet<int>();
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                if (Overlaps(
                        startAt(items[i]), durationMinutes(items[i]),
                        startAt(items[j]), durationMinutes(items[j])))
                {
                    conflicted.Add(i);
                    conflicted.Add(j);
                }
            }
        }

        return conflicted;
    }
}
