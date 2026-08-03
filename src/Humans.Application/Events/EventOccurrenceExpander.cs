using Humans.Application.Interfaces.Events;
using NodaTime;

namespace Humans.Application.Events;

/// <summary>
/// One expanded occurrence of an approved event for the Browse page: a
/// recurring event yields one entry per matching day, a non-recurring (or
/// unexpanded) event yields itself.
/// </summary>
public sealed record EventOccurrence(
    ApprovedEventView Event,
    Instant StartAt,
    int DayOffset,
    int? FavouriteDayOffset,
    bool IsFavourited);

/// <summary>
/// Single owner of the Browse-page occurrence rules: expanding recurring
/// events into per-day occurrences, resolving each occurrence's gate-relative
/// day offset, applying the day filter, and resolving favourite state.
/// </summary>
public static class EventOccurrenceExpander
{
    public static IReadOnlyList<EventOccurrence> Expand(
        IReadOnlyList<ApprovedEventView> events,
        ILookup<Guid, int?> favouriteDaysByEventId,
        LocalDate? gateOpeningDate,
        DateTimeZone? timeZone,
        IReadOnlySet<int>? filterDays)
    {
        var items = new List<EventOccurrence>();
        foreach (var e in events)
        {
            IReadOnlyList<Instant> occurrences = gateOpeningDate.HasValue && timeZone != null
                ? e.GetOccurrenceInstants(gateOpeningDate.Value, timeZone)
                : [e.StartAt];

            foreach (var startInstant in occurrences)
            {
                var dayOffset = ResolveDayOffset(startInstant, gateOpeningDate, timeZone);
                if (filterDays != null && !filterDays.Contains(dayOffset)) continue;

                // Hearts on recurring-event cards favourite that day's occurrence;
                // non-recurring cards (and unexpanded ones) favourite the whole event.
                var favouriteDayOffset = e.IsRecurring && gateOpeningDate.HasValue && timeZone != null
                    ? dayOffset
                    : (int?)null;

                items.Add(new EventOccurrence(
                    Event: e,
                    StartAt: startInstant,
                    DayOffset: dayOffset,
                    FavouriteDayOffset: favouriteDayOffset,
                    IsFavourited: favouriteDaysByEventId[e.Id].Any(d => d == null || d == favouriteDayOffset)));
            }
        }

        return items;
    }

    private static int ResolveDayOffset(Instant startInstant, LocalDate? gateOpeningDate, DateTimeZone? timeZone)
    {
        if (gateOpeningDate == null) return 0;

        var eventDate = timeZone != null
            ? startInstant.InZone(timeZone).Date
            : LocalDate.FromDateTime(startInstant.ToDateTimeUtc());
        return Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
    }
}
