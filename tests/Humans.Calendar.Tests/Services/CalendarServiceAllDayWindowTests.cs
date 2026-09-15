using AwesomeAssertions;
using Humans.Calendar.Services;
using NodaTime;
using Xunit;

namespace Humans.Calendar.Tests.Services;

/// <summary>All-day forms round-trip an inclusive last day through an exclusive date.</summary>
public sealed class CalendarServiceAllDayWindowTests
{

    [HumansTheory]
    // A one-day event: the stored end is the following midnight, not the same one.
    [InlineData(2026, 6, 10, 2026, 6, 10)]
    // Three days.
    [InlineData(2026, 6, 10, 2026, 6, 12)]
    // Across Madrid's spring forward (29 March 2026), where the covered day is 23 hours long.
    [InlineData(2026, 3, 28, 2026, 3, 30)]
    // Across the autumn transition (25 October 2026), where it is 25 hours long.
    [InlineData(2026, 10, 24, 2026, 10, 26)]
    public void AllDayWindow_round_trips_through_its_inverse(
        int startYear, int startMonth, int startDay,
        int endYear, int endMonth, int endDay)
    {
        var start = new LocalDate(startYear, startMonth, startDay);
        var inclusiveEnd = new LocalDate(endYear, endMonth, endDay);

        var (startUtc, endUtc) = CalendarService.AllDayWindow(start, inclusiveEnd);

        // Both ends remain dates whatever the day's length.
        startUtc.Should().Be(start);
        endUtc.Should().Be(inclusiveEnd.PlusDays(1));

        CalendarService.AllDayInclusiveEndDate(endUtc).Should().Be(inclusiveEnd);
    }

    [HumansFact]
    public void AllDayInclusiveEndDate_collapses_an_exclusive_midnight_to_the_previous_day()
    {
        // The off-by-one this guards: 11 June 00:00 stored means the event's last day is the 10th.
        var exclusiveEnd = new LocalDate(2026, 6, 11);

        CalendarService.AllDayInclusiveEndDate(exclusiveEnd)
            .Should().Be(new LocalDate(2026, 6, 10));
    }
}
