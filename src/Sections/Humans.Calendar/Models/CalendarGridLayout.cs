using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal static class CalendarGridLayout
{
    public const int MaxPerCell = 3;
    public const int MaxBannerSlots = 3;

    /// <summary>
    /// Inclusive, with the adjacent-month days padding the first and last weeks. The controller queries this range
    /// and <c>Index.cshtml</c> lays it out, so the padding cells get their events too.
    /// </summary>
    public static (LocalDate GridStart, LocalDate GridEnd) MonthGridBounds(YearMonth month)
    {
        var firstOfMonth = month.OnDayOfMonth(1);
        var lastOfMonth = month.OnDayOfMonth(
            firstOfMonth.Calendar.GetDaysInMonth(month.Year, month.Month));

        return (firstOfMonth.PlusDays(-LeadOffset(firstOfMonth)),
                lastOfMonth.PlusDays(6 - LeadOffset(lastOfMonth)));
    }

    /// <summary><c>IsoDayOfWeek.Monday</c> is 1, Sunday 7.</summary>
    private static int LeadOffset(LocalDate d) => ((int)d.DayOfWeek + 6) % 7;

    public static WeekLayout BuildWeekLayout(
        LocalDate weekStart,
        IReadOnlyList<CalendarOccurrence> weekOccurrences,
        DateTimeZone zone)
    {
        var multiDay = new List<BannerPlacement>();
        var singleDayByDow = new List<CalendarOccurrence>[7];
        for (var i = 0; i < 7; i++) singleDayByDow[i] = [];

        // Earliest start, then longest, so banners stack deterministically.
        var ordered = weekOccurrences
            .OrderBy(o => o.StartLocalDate(zone))
            .ThenBy(o => o.OccurrenceStartUtc)
            .ThenByDescending(o => o.IsAllDay
                ? Period.Between(o.StartDate!.Value, o.EndDateExclusive!.Value, PeriodUnits.Days).Days * NodaConstants.TicksPerDay
                : (o.OccurrenceEndUtc ?? o.OccurrenceStartUtc!.Value).ToUnixTimeTicks() - o.OccurrenceStartUtc!.Value.ToUnixTimeTicks())
            .ToList();

        foreach (var o in ordered)
        {
            var startDate = o.StartLocalDate(zone);
            var endDate = o.EndLocalDate(zone);

            var coversMultipleDays = endDate > startDate;

            if (!coversMultipleDays)
            {
                var dow = Period.Between(weekStart, startDate, PeriodUnits.Days).Days;
                if (dow >= 0 && dow < 7)
                {
                    singleDayByDow[dow].Add(o);
                }
                continue;
            }

            // The caller pre-filters to occurrences overlapping the week, so the clip stays inside it.
            var weekEnd = weekStart.PlusDays(6);
            var clipStart = startDate < weekStart ? weekStart : startDate;
            var clipEnd = endDate > weekEnd ? weekEnd : endDate;

            var startDow = Period.Between(weekStart, clipStart, PeriodUnits.Days).Days;
            var endDow = Period.Between(weekStart, clipEnd, PeriodUnits.Days).Days;

            multiDay.Add(new BannerPlacement(
                Occurrence: o,
                StartDow: startDow,
                EndDow: endDow,
                ContinuesFromPreviousWeek: startDate < weekStart,
                ContinuesIntoNextWeek: endDate > weekEnd,
                SlotIndex: 0));
        }

        var placed = new List<BannerPlacement>();
        var slots = new List<List<BannerPlacement>>();
        foreach (var b in multiDay)
        {
            var assigned = false;
            for (var s = 0; s < slots.Count && s < MaxBannerSlots; s++)
            {
                var conflicts = slots[s].Any(existing => !(b.EndDow < existing.StartDow || b.StartDow > existing.EndDow));
                if (!conflicts)
                {
                    slots[s].Add(b);
                    placed.Add(b with { SlotIndex = s });
                    assigned = true;
                    break;
                }
            }
            if (!assigned && slots.Count < MaxBannerSlots)
            {
                var idx = slots.Count;
                slots.Add([b]);
                placed.Add(b with { SlotIndex = idx });
                assigned = true;
            }
            // No slot left: the banner counts toward each covered day's "+N more".
            if (!assigned)
            {
                for (var d = b.StartDow; d <= b.EndDow; d++)
                {
                    singleDayByDow[d].Add(b.Occurrence);
                }
            }
        }

        return new WeekLayout(
            WeekStart: weekStart,
            Banners: placed,
            SingleDayOccurrencesByDow: singleDayByDow.Select(l => (IReadOnlyList<CalendarOccurrence>)l).ToList());
    }
}

internal sealed record BannerPlacement(
    CalendarOccurrence Occurrence,
    int StartDow,
    int EndDow,
    bool ContinuesFromPreviousWeek,
    bool ContinuesIntoNextWeek,
    int SlotIndex);

internal sealed record WeekLayout(
    LocalDate WeekStart,
    IReadOnlyList<BannerPlacement> Banners,
    IReadOnlyList<IReadOnlyList<CalendarOccurrence>> SingleDayOccurrencesByDow);
