using AwesomeAssertions;
using Humans.Calendar.Services;
using NodaTime;
using Xunit;

namespace Humans.Calendar.Tests.Services;

/// <summary>
/// All-day events are stored half-open — [start local midnight, the day after the last covered
/// day) — while the create/edit form and every view speak in the inclusive last day. The
/// conversion used to be written out by hand at each call site inside `CalendarController`,
/// which is why only its display half had a test.
/// </summary>
public sealed class CalendarServiceAllDayWindowTests
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

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

        var (startUtc, endUtc) = CalendarService.AllDayWindow(start, inclusiveEnd, Madrid);

        // Both ends sit on local midnight whatever the day's length.
        startUtc.InZone(Madrid).LocalDateTime.Should().Be(start.AtMidnight());
        endUtc.InZone(Madrid).LocalDateTime.Should().Be(inclusiveEnd.PlusDays(1).AtMidnight());

        CalendarService.AllDayInclusiveEndDate(endUtc, Madrid).Should().Be(inclusiveEnd);
    }

    [HumansFact]
    public void AllDayInclusiveEndDate_collapses_an_exclusive_midnight_to_the_previous_day()
    {
        // The off-by-one this guards: 11 June 00:00 stored means the event's last day is the 10th.
        var exclusiveEnd = new LocalDate(2026, 6, 11).AtMidnight().InZoneLeniently(Madrid).ToInstant();

        CalendarService.AllDayInclusiveEndDate(exclusiveEnd, Madrid)
            .Should().Be(new LocalDate(2026, 6, 10));
    }
}
