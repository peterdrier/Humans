using AwesomeAssertions;
using Humans.Calendar.Models;
using Humans.Calendar.Services.Dtos;
using NodaTime;
using Xunit;

namespace Humans.Calendar.Tests.Models;

public sealed class CalendarGridLayoutTests
{
    private static readonly LocalDate WeekStart = new(2026, 6, 1);

    // The controller queries this range and Index.cshtml lays it out. While each computed it
    // separately the query covered only the month's own days, so every leading and trailing
    // cell was structurally empty — an event on 31 August could not appear in September.
    [HumansTheory]
    // September 2026 starts on a Tuesday and ends on a Wednesday.
    [InlineData(2026, 9, 2026, 8, 31, 2026, 10, 4)]
    // February 2027 starts on a Monday, so there is no leading pad.
    [InlineData(2027, 2, 2027, 2, 1, 2027, 2, 28)]
    // A month ending on a Sunday needs no trailing pad either.
    [InlineData(2026, 5, 2026, 4, 27, 2026, 5, 31)]
    public void MonthGridBounds_Covers_every_cell_the_grid_renders(
        int year, int month,
        int startYear, int startMonth, int startDay,
        int endYear, int endMonth, int endDay)
    {
        var (gridStart, gridEnd) = CalendarGridLayout.MonthGridBounds(new YearMonth(year, month));

        gridStart.Should().Be(new LocalDate(startYear, startMonth, startDay));
        gridEnd.Should().Be(new LocalDate(endYear, endMonth, endDay));

        // Monday-first, whole weeks: the grid is always a multiple of seven days.
        gridStart.DayOfWeek.Should().Be(IsoDayOfWeek.Monday);
        gridEnd.DayOfWeek.Should().Be(IsoDayOfWeek.Sunday);
        (Period.Between(gridStart, gridEnd.PlusDays(1), PeriodUnits.Days).Days % 7).Should().Be(0);
    }

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
