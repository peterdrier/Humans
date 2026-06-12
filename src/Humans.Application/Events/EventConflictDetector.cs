using NodaTime;

namespace Humans.Application.Events;

/// <summary>
/// Single owner of the "two events overlap in time" rule (audit E12/E13):
/// half-open [start, start + duration) interval intersection. Used by the
/// personal-schedule conflict flags and the moderation duplicate-candidate scan.
/// </summary>
public static class EventConflictDetector
{
    public static bool Overlaps(
        Instant aStartAt, int aDurationMinutes,
        Instant bStartAt, int bDurationMinutes) =>
        aStartAt < bStartAt.Plus(Duration.FromMinutes(bDurationMinutes))
        && bStartAt < aStartAt.Plus(Duration.FromMinutes(aDurationMinutes));

    /// <summary>
    /// Indexes of every item that overlaps at least one other item.
    /// O(n²) — input sets are small (one user's schedule, one camp's events).
    /// </summary>
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
