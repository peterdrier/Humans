using AwesomeAssertions;
using Humans.Calendar.Models;
using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Tests.Models;

public sealed class CalendarGridLayoutTests
{
    private static readonly LocalDate WeekStart = new(2026, 6, 1);

    [HumansFact]
    public void BuildWeekLayout_Reuses_the_lowest_non_conflicting_banner_slot()
    {
        var layout = CalendarGridLayout.BuildWeekLayout(
            WeekStart,
            [
                Occurrence("first", day: 0, endDay: 1),
                Occurrence("second", day: 1, endDay: 2),
                Occurrence("third", day: 2, endDay: 3),
            ],
            DateTimeZone.Utc);

        layout.Banners.Select(b => (b.Occurrence.Title, b.SlotIndex))
            .Should()
            .BeEquivalentTo(
                [("first", 0), ("second", 1), ("third", 0)],
                options => options.WithStrictOrdering());
    }

    [HumansFact]
    public void BuildWeekLayout_Puts_overflowed_banners_in_each_covered_day_cell()
    {
        var layout = CalendarGridLayout.BuildWeekLayout(
            WeekStart,
            [
                Occurrence("first", day: 0, endDay: 1),
                Occurrence("second", day: 0, endDay: 1),
                Occurrence("third", day: 0, endDay: 1),
                Occurrence("overflow", day: 0, endDay: 1),
            ],
            DateTimeZone.Utc);

        layout.Banners.Should().HaveCount(CalendarGridLayout.MaxBannerSlots);
        layout.SingleDayOccurrencesByDow[0].Select(o => o.Title).Should().Equal("overflow");
        layout.SingleDayOccurrencesByDow[1].Select(o => o.Title).Should().Equal("overflow");
        layout.SingleDayOccurrencesByDow.Skip(2).Should().AllSatisfy(day => day.Should().BeEmpty());
    }

    private static CalendarOccurrence Occurrence(string title, int day, int endDay) => new(
        EventId: Guid.NewGuid(),
        OccurrenceStartUtc: WeekStart.PlusDays(day).At(new LocalTime(10, 0)).InUtc().ToInstant(),
        OccurrenceEndUtc: WeekStart.PlusDays(endDay).At(new LocalTime(11, 0)).InUtc().ToInstant(),
        IsAllDay: false,
        Title: title,
        Description: null,
        Location: null,
        LocationUrl: null,
        OwningTeamId: Guid.NewGuid(),
        OwningTeamName: "Calendar team",
        IsRecurring: false,
        OriginalOccurrenceStartUtc: null);
}
