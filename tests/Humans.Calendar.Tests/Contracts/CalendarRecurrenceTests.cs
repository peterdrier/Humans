using AwesomeAssertions;
using Humans.Calendar.Contracts;
using NodaTime;
using Xunit;

namespace Humans.Calendar.Tests.Contracts;

/// <summary>The expansion other sections use for their own repeating items.</summary>
public sealed class CalendarRecurrenceTests
{
    [HumansFact]
    public void A_weekly_rule_keeps_its_local_time_across_daylight_saving()
    {
        // 19:00 Madrid is 17:00Z in summer and 18:00Z after the clocks go back on 25 October.
        var first = Instant.FromUtc(2026, 10, 20, 17, 0);

        var occurrences = CalendarRecurrence.Occurrences(first, first.Plus(Duration.FromHours(1)), "FREQ=WEEKLY",
            "Europe/Madrid", Instant.FromUtc(2026, 10, 1, 0, 0), Instant.FromUtc(2026, 11, 1, 0, 0));

        occurrences.Select(o => o.Start).Should().Equal(first, Instant.FromUtc(2026, 10, 27, 18, 0));
    }

    [HumansFact]
    public void A_one_off_item_is_its_own_occurrence_only_inside_the_window()
    {
        var start = Instant.FromUtc(2026, 6, 2, 10, 0);
        var end = start.Plus(Duration.FromHours(1));

        CalendarRecurrence.Occurrences(start, end, null, null, start.Minus(Duration.FromDays(1)), end)
            .Should().ContainSingle().Which.Should().Be(new Interval(start, end));
        CalendarRecurrence.Occurrences(start, end, null, null, end, end.Plus(Duration.FromDays(1)))
            .Should().BeEmpty();
    }
}
