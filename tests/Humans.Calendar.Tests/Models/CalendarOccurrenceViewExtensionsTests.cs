using AwesomeAssertions;
using Humans.Calendar.Models;
using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Tests.Models;

public sealed class CalendarOccurrenceViewExtensionsTests
{
    [HumansFact]
    public void EndLocalDate_Treats_a_midnight_end_as_half_open()
    {
        var occurrence = Occurrence(
            Instant.FromUtc(2026, 6, 1, 10, 0),
            Instant.FromUtc(2026, 6, 2, 0, 0));

        occurrence.EndLocalDate(DateTimeZone.Utc).Should().Be(new LocalDate(2026, 6, 1));
        occurrence.ShouldHideTimeLabel(DateTimeZone.Utc).Should().BeFalse();
    }

    [HumansFact]
    public void ShouldHideTimeLabel_Hides_a_timed_event_that_spans_local_dates()
    {
        var occurrence = Occurrence(
            Instant.FromUtc(2026, 6, 1, 21, 0),
            Instant.FromUtc(2026, 6, 2, 1, 0));
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

        occurrence.StartLocalDate(madrid).Should().Be(new LocalDate(2026, 6, 1));
        occurrence.EndLocalDate(madrid).Should().Be(new LocalDate(2026, 6, 2));
        occurrence.ShouldHideTimeLabel(madrid).Should().BeTrue();
    }

    private static CalendarOccurrence Occurrence(Instant start, Instant end) => new(
        EventId: Guid.NewGuid(),
        OccurrenceStartUtc: start,
        OccurrenceEndUtc: end,
        IsAllDay: false,
        Title: "Calendar event",
        Description: null,
        Location: null,
        LocationUrl: null,
        OwningTeamId: Guid.NewGuid(),
        OwningTeamName: "Calendar team",
        IsRecurring: false,
        OriginalOccurrenceStartUtc: null);
}
