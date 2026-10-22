using NodaTime;
using Humans.Events.Contracts;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.Services;

/// <summary>A recurring event yields one per matching day; a non-recurring or unexpanded event yields itself.</summary>
internal sealed record EventOccurrence(
    ApprovedEventView Event,
    Instant StartAt,
    int DayOffset,
    int? FavouriteDayOffset,
    bool IsFavourited);

internal static class EventOccurrenceExpander
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
            IReadOnlyList<Instant> occurrences = e.GetOccurrenceInstants(gateOpeningDate, timeZone);

            foreach (var startInstant in occurrences)
            {
                var dayOffset = ComputeDayOffset(startInstant, gateOpeningDate, timeZone);
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
}
