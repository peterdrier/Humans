using Humans.Application.DTOs.Calendar;
using NodaTime;

namespace Humans.Web.Models.Calendar;

public static class CalendarGridLayout
{
    public const int MaxPerCell = 3;
    public const int MaxBannerSlots = 3;

    public static WeekLayout BuildWeekLayout(
        LocalDate weekStart,
        IReadOnlyList<CalendarOccurrence> weekOccurrences,
        DateTimeZone zone)
    {
        // Determine each occurrence's local start/end dates (inclusive end for display).
        var multiDay = new List<BannerPlacement>();
        var singleDayByDow = new List<CalendarOccurrence>[7];
        for (var i = 0; i < 7; i++) singleDayByDow[i] = new List<CalendarOccurrence>();

        // Sort: earliest-start, longest-duration first so banners stack deterministically.
        var ordered = weekOccurrences
            .OrderBy(o => o.OccurrenceStartUtc)
            .ThenByDescending(o => (o.OccurrenceEndUtc ?? o.OccurrenceStartUtc).ToUnixTimeTicks() - o.OccurrenceStartUtc.ToUnixTimeTicks())
            .ToList();

        foreach (var o in ordered)
        {
            var startDate = o.StartLocalDate(zone);
            var endDate = o.EndLocalDate(zone);

            // Determine if this should render as a banner (multi-day covering >1 day in this week,
            // OR an all-day event that covers the week at all).
            var coversMultipleDays = endDate > startDate;

            if (!coversMultipleDays)
            {
                // Single-day timed event → regular per-cell render if the day is in this week.
                var dow = Period.Between(weekStart, startDate, PeriodUnits.Days).Days;
                if (dow >= 0 && dow < 7)
                {
                    singleDayByDow[dow].Add(o);
                }
                continue;
            }

            // Clip the banner to this week's visible window. Caller pre-filters to
            // occurrences that overlap the week, so clipStart/clipEnd are always inside [weekStart, weekEnd].
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

        // Greedy slot assignment: walk banners in chronological order, put each in the
        // lowest slot index that doesn't conflict (overlap) with any banner already in
        // that slot in this week.
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
                slots.Add(new List<BannerPlacement> { b });
                placed.Add(b with { SlotIndex = idx });
                assigned = true;
            }
            // If there's no slot left, the banner overflows — count it toward each covered day's
            // "+N more" total by pushing it into singleDayByDow of each covered day.
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

/// <summary>Where a multi-day banner lives inside a week row.</summary>
public sealed record BannerPlacement(
    CalendarOccurrence Occurrence,
    int StartDow,
    int EndDow,
    bool ContinuesFromPreviousWeek,
    bool ContinuesIntoNextWeek,
    int SlotIndex);

public sealed record WeekLayout(
    LocalDate WeekStart,
    IReadOnlyList<BannerPlacement> Banners,
    IReadOnlyList<IReadOnlyList<CalendarOccurrence>> SingleDayOccurrencesByDow);
