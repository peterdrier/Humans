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

    // The badge and the hide-the-time decision are not the same question. A 22:00–02:00 event
    // spans two local dates, so its time label would mislead on the second day — but on the
    // first it is emphatically not an all-day event, and 22:00 is the only time it has.
    [HumansFact]
    public void TimeLabelFor_ShowsTheStartTime_OnTheFirstDayOfATimedMultiDayEvent()
    {
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var occurrence = Occurrence(
            Instant.FromUtc(2026, 6, 1, 20, 0),
            Instant.FromUtc(2026, 6, 2, 0, 30));

        occurrence.ShouldHideTimeLabel(madrid).Should().BeTrue();
        occurrence.TimeLabelFor(new LocalDate(2026, 6, 1), madrid)
            .Should().Be(OccurrenceTimeLabel.StartTime);
        occurrence.TimeLabelFor(new LocalDate(2026, 6, 2), madrid)
            .Should().Be(OccurrenceTimeLabel.Continues);
    }

    [HumansFact]
    public void TimeLabelFor_MarksAnAllDayEvent_AllDayThenContinues()
    {
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var occurrence = Occurrence(
            Instant.FromUtc(2026, 5, 31, 22, 0),
            Instant.FromUtc(2026, 6, 3, 22, 0),
            isAllDay: true);

        occurrence.TimeLabelFor(new LocalDate(2026, 6, 1), madrid)
            .Should().Be(OccurrenceTimeLabel.AllDay);
        occurrence.TimeLabelFor(new LocalDate(2026, 6, 3), madrid)
            .Should().Be(OccurrenceTimeLabel.Continues);
    }

    [HumansFact]
    public void TimeLabelFor_ShowsTheStartTime_ForAnOrdinarySingleDayEvent()
    {
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var occurrence = Occurrence(
            Instant.FromUtc(2026, 6, 1, 17, 0),
            Instant.FromUtc(2026, 6, 1, 18, 0));

        occurrence.TimeLabelFor(new LocalDate(2026, 6, 1), madrid)
            .Should().Be(OccurrenceTimeLabel.StartTime);
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

    // A 3-day all-day event is stored half-open: 1 Jun 00:00 .. 4 Jun 00:00 local. The
    // display layer must recover 1–3 Jun inclusive. Off by one here and the last day of
    // every all-day event either disappears or a spurious day appears — and List.cshtml
    // and Team.cshtml now both drive their day expansion off exactly this pair.
    [HumansFact]
    public void All_day_event_recovers_its_inclusive_end_date()
    {
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var occurrence = Occurrence(
            new LocalDate(2026, 6, 1).AtMidnight().InZoneLeniently(madrid).ToInstant(),
            new LocalDate(2026, 6, 4).AtMidnight().InZoneLeniently(madrid).ToInstant(),
            isAllDay: true);

        occurrence.StartLocalDate(madrid).Should().Be(new LocalDate(2026, 6, 1));
        occurrence.EndLocalDate(madrid).Should().Be(new LocalDate(2026, 6, 3));
        occurrence.ShouldHideTimeLabel(madrid).Should().BeTrue();
    }

    // Spring-forward: 29 Mar 2026 is 23 hours long in Madrid. The inclusive end must still
    // be the 29th, not the 28th — subtracting a fixed 24h instead of a tick would fail this.
    [HumansFact]
    public void All_day_event_spanning_a_DST_transition_keeps_its_inclusive_end_date()
    {
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var occurrence = Occurrence(
            new LocalDate(2026, 3, 28).AtMidnight().InZoneLeniently(madrid).ToInstant(),
            new LocalDate(2026, 3, 30).AtMidnight().InZoneLeniently(madrid).ToInstant(),
            isAllDay: true);

        occurrence.EndLocalDate(madrid).Should().Be(new LocalDate(2026, 3, 29));
    }

    // Legacy single-day all-day rows predate the exclusive-end convention and carry a null
    // end. They must collapse to their start date, not to "no end".
    [HumansFact]
    public void All_day_event_with_a_null_end_collapses_to_its_start_date()
    {
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var occurrence = Occurrence(
            new LocalDate(2026, 6, 1).AtMidnight().InZoneLeniently(madrid).ToInstant(),
            end: null,
            isAllDay: true);

        occurrence.EndLocalDate(madrid).Should().Be(new LocalDate(2026, 6, 1));
        occurrence.ShouldHideTimeLabel(madrid).Should().BeTrue();
    }

    private static CalendarOccurrence Occurrence(Instant start, Instant? end, bool isAllDay = false) => new(
        EventId: Guid.NewGuid(),
        OccurrenceStartUtc: start,
        OccurrenceEndUtc: end,
        IsAllDay: isAllDay,
        Title: "Calendar event",
        Description: null,
        Location: null,
        LocationUrl: null,
        OwningTeamId: Guid.NewGuid(),
        OwningTeamName: "Calendar team",
        IsRecurring: false,
        OriginalOccurrenceStartUtc: null);
}
