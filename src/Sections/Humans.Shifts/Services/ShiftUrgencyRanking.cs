using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using NodaTime;

namespace Humans.Shifts.Services;

internal static class ShiftUrgencyRanking
{
    private static readonly Dictionary<ShiftPriority, double> PriorityWeights = new()
    {
        [ShiftPriority.Normal] = 1,
        [ShiftPriority.Important] = 3,
        [ShiftPriority.Essential] = 6
    };

    internal static double CalculateScore(Shift shift, int confirmedCount, IEventSettingsInfo eventSettings, IClock clock)
    {
        var remainingSlots = Math.Max(0, shift.MaxVolunteers - confirmedCount);
        if (remainingSlots == 0) return 0;

        var priorityWeight = PriorityWeights.GetValueOrDefault(shift.Rota?.Priority ?? ShiftPriority.Normal, 1);
        var durationHours = shift.IsAllDay ? Shift.AllDayWindowHours : shift.Duration.TotalHours;
        var understaffedMultiplier = confirmedCount < shift.MinVolunteers ? 2 : 1;

        // Proximity boost 1 + 10/(1+days): today 11x, tomorrow 6x, a week 2.25x, a month 1.32x.
        var now = clock.GetCurrentInstant();
        var shiftStart = shift.GetAbsoluteStart(eventSettings);
        var daysUntilStart = Math.Max(0, (shiftStart - now).TotalDays);
        var proximityBoost = 1.0 + (10.0 / (1.0 + daysUntilStart));

        return remainingSlots * priorityWeight * durationHours * understaffedMultiplier * proximityBoost;
    }

    /// <summary>Top-N with period diversity: one slot per non-Build period if eligible, rest from top scorers.</summary>
    internal static List<UrgentShiftInfo> ApplyPeriodDiverseLimit(
        List<UrgentShiftInfo> rankedShifts, int limit)
    {
        if (rankedShifts.Count <= limit)
            return rankedShifts;

        var byPeriod = rankedShifts
            .GroupBy(u => u.Period)
            .ToDictionary(g => g.Key, g => g.ToList());

        var reserved = new List<UrgentShiftInfo>();
        var reservedIds = new HashSet<Guid>();
        foreach (var period in new[] { ShiftPeriod.Event, ShiftPeriod.Strike })
        {
            if (byPeriod.TryGetValue(period, out var periodShifts) && periodShifts.Count > 0)
            {
                var best = periodShifts[0];
                reserved.Add(best);
                reservedIds.Add(best.Shift.Id);
            }
        }

        if (reserved.Count >= limit)
            return reserved.OrderByDescending(u => u.UrgencyScore).Take(limit).ToList();

        var result = new List<UrgentShiftInfo>(reserved);
        foreach (var shift in rankedShifts)
        {
            if (result.Count >= limit) break;
            if (!reservedIds.Contains(shift.Shift.Id))
                result.Add(shift);
        }

        return result.OrderByDescending(u => u.UrgencyScore).ToList();
    }
}
