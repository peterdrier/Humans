using Humans.Calendar.Services.Dtos;
using AwesomeAssertions;
using Humans.Calendar.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Humans.Calendar.Tests.Services;

public sealed class CalendarOccurrenceExpanderTests
{
    [HumansFact]
    public void Expand_DropsCancelledRecurringOccurrence()
    {
        var eventId = Guid.NewGuid();
        var cancelledStart = Instant.FromUtc(2026, 6, 2, 10, 0);
        var info = BuildInfo(
            id: eventId,
            start: Instant.FromUtc(2026, 6, 1, 10, 0),
            end: Instant.FromUtc(2026, 6, 1, 11, 0),
            recurrenceRule: "FREQ=DAILY;COUNT=3",
            exceptions:
            [
                new CalendarEventExceptionInfo(
                    Id: Guid.NewGuid(),
                    OriginalOccurrenceStartUtc: cancelledStart,
                    IsCancelled: true,
                    OverrideStartUtc: null,
                    OverrideEndUtc: null,
                    OverrideTitle: null,
                    OverrideDescription: null,
                    OverrideLocation: null,
                    OverrideLocationUrl: null),
            ]);

        var results = CalendarOccurrenceExpander.Expand(
            [info],
            Instant.FromUtc(2026, 6, 1, 0, 0),
            Instant.FromUtc(2026, 6, 4, 0, 0),
            new Dictionary<Guid, string>(),
            NullLogger.Instance);

        results.Select(x => x.OccurrenceStartUtc)
            .Should()
            .Equal(
                Instant.FromUtc(2026, 6, 1, 10, 0),
                Instant.FromUtc(2026, 6, 3, 10, 0));
    }

    [HumansFact]
    public void Expand_IncludesOverrideMovedIntoWindow()
    {
        var eventId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var originalStart = Instant.FromUtc(2026, 6, 1, 10, 0);
        var movedStart = Instant.FromUtc(2026, 6, 5, 14, 0);
        var info = BuildInfo(
            id: eventId,
            teamId: teamId,
            title: "Original",
            start: originalStart,
            end: Instant.FromUtc(2026, 6, 1, 11, 0),
            recurrenceRule: "FREQ=DAILY;COUNT=2",
            exceptions:
            [
                new CalendarEventExceptionInfo(
                    Id: Guid.NewGuid(),
                    OriginalOccurrenceStartUtc: originalStart,
                    IsCancelled: false,
                    OverrideStartUtc: movedStart,
                    OverrideEndUtc: Instant.FromUtc(2026, 6, 5, 16, 0),
                    OverrideTitle: "Moved",
                    OverrideDescription: null,
                    OverrideLocation: null,
                    OverrideLocationUrl: null),
            ]);

        var results = CalendarOccurrenceExpander.Expand(
            [info],
            Instant.FromUtc(2026, 6, 5, 0, 0),
            Instant.FromUtc(2026, 6, 6, 0, 0),
            new Dictionary<Guid, string> { [teamId] = "Calendar Team" },
            NullLogger.Instance);

        var result = results.Should().ContainSingle().Subject;
        result.EventId.Should().Be(eventId);
        result.Title.Should().Be("Moved");
        result.OccurrenceStartUtc.Should().Be(movedStart);
        result.OccurrenceEndUtc.Should().Be(Instant.FromUtc(2026, 6, 5, 16, 0));
        result.OriginalOccurrenceStartUtc.Should().Be(originalStart);
        result.OwningTeamName.Should().Be("Calendar Team");
    }

    [HumansFact]
    public void Expand_LegacyAllDayRecurrence_RemainsOneDayAcrossSpringForward()
    {
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var day = new LocalDate(2026, 3, 28);
        var info = CalendarOccurrenceExpander.ToInfo(new Humans.Calendar.Domain.CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "All day",
            IsAllDay = true,
            StartUtc = day.AtStartOfDayInZone(zone).ToInstant(),
            EndUtc = day.PlusDays(1).AtStartOfDayInZone(zone).ToInstant(),
            RecurrenceRule = "FREQ=DAILY;COUNT=5",
            RecurrenceTimezone = zone.Id,
        });
        var results = CalendarOccurrenceExpander.Expand([info],
            day.AtStartOfDayInZone(zone).ToInstant(),
            day.PlusDays(5).AtStartOfDayInZone(zone).ToInstant(),
            new Dictionary<Guid, string>(), NullLogger.Instance);

        results.Should().HaveCount(5);
        foreach (var occurrence in results)
            Humans.Calendar.Models.CalendarOccurrenceViewExtensions.EndLocalDate(occurrence, zone)
                .Should().Be(Humans.Calendar.Models.CalendarOccurrenceViewExtensions.StartLocalDate(occurrence, zone));
    }

    [HumansTheory]
    [Xunit.InlineData(3, 28)]
    [Xunit.InlineData(10, 24)]
    public void Expand_DateRecurrences_PreserveCalendarDurationAndIgnoreViewerZone(int month, int day)
    {
        var first = new LocalDate(2026, month, day);
        var info = BuildInfo(recurrenceRule: "FREQ=DAILY;COUNT=5") with
        {
            IsAllDay = true,
            StartUtc = null,
            EndUtc = null,
            RecurrenceTimezone = null,
            StartDate = first,
            EndDateExclusive = first.PlusDays(2),
        };
        var madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var results = CalendarOccurrenceExpander.Expand([info], first.AtStartOfDayInZone(madrid).ToInstant(),
            first.PlusDays(6).AtStartOfDayInZone(madrid).ToInstant(), new Dictionary<Guid, string>(), NullLogger.Instance);
        results.Should().HaveCount(5);
        for (var n = 0; n < results.Count; n++)
        {
            var occurrence = results[n];
            occurrence.StartDate.Should().Be(first.PlusDays(n));
            occurrence.EndDateExclusive.Should().Be(first.PlusDays(n + 2));
            occurrence.OccurrenceStartUtc.Should().BeNull();
            occurrence.OccurrenceEndUtc.Should().BeNull();
            Humans.Calendar.Models.CalendarOccurrenceViewExtensions.StartLocalDate(occurrence, DateTimeZoneProviders.Tzdb["America/Los_Angeles"])
                .Should().Be(first.PlusDays(n));
        }
    }

    [HumansFact]
    public void Expand_DateUntil_IncludesLastDayAndOverlapsAfterIt()
    {
        var info = BuildInfo(recurrenceRule: "FREQ=DAILY;UNTIL=20260329") with
        {
            IsAllDay = true,
            StartUtc = null,
            EndUtc = null,
            RecurrenceTimezone = null,
            StartDate = new LocalDate(2026, 3, 28),
            EndDateExclusive = new LocalDate(2026, 3, 30),
        };
        var results = CalendarOccurrenceExpander.Expand([info], Instant.FromUtc(2026, 3, 30, 0, 0),
            Instant.FromUtc(2026, 3, 31, 0, 0), new Dictionary<Guid, string>(), NullLogger.Instance);
        var occurrence = results.Should().ContainSingle().Subject;
        occurrence.StartDate.Should().Be(new LocalDate(2026, 3, 29));
        occurrence.EndDateExclusive.Should().Be(new LocalDate(2026, 3, 31));
    }

    [HumansFact]
    public void Expand_DateOverrideMovedBeforeSeries_SurvivesPrefilterAndPreservesDuration()
    {
        var date = new LocalDate(2026, 6, 10);
        var info = BuildInfo(recurrenceRule: "FREQ=DAILY;COUNT=2") with
        {
            IsAllDay = true,
            StartUtc = null,
            EndUtc = null,
            RecurrenceTimezone = null,
            StartDate = date,
            EndDateExclusive = date.PlusDays(2),
            RecurrenceUntilDate = date.PlusDays(3),
            Exceptions = [new CalendarEventExceptionInfo(Guid.NewGuid(), null, false, null, null,
                "Moved", null, null, null, date, new LocalDate(2026, 3, 29))],
        };
        var from = Instant.FromUtc(2026, 3, 29, 0, 0);
        var to = Instant.FromUtc(2026, 3, 30, 0, 0);
        var filtered = CalendarOccurrenceExpander.FilterForWindow([info], from, to, null);
        var result = CalendarOccurrenceExpander.Expand(filtered, from, to, new Dictionary<Guid, string>(), NullLogger.Instance)
            .Should().ContainSingle().Subject;
        result.StartDate.Should().Be(new LocalDate(2026, 3, 29));
        result.EndDateExclusive.Should().Be(new LocalDate(2026, 3, 31));
        result.OriginalOccurrenceDate.Should().Be(date);
        result.Title.Should().Be("Moved");
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void Expand_ShortenedSeries_DoesNotResurrectRetitledOccurrence(bool allDay, bool unchangedStart)
    {
        var first = new LocalDate(2026, 9, 1);
        var firstInstant = Instant.FromUtc(2026, 9, 1, 10, 0);
        var removedInstant = Instant.FromUtc(2026, 9, 29, 10, 0);
        var info = BuildInfo(start: firstInstant, end: firstInstant.Plus(Duration.FromHours(1)),
            recurrenceRule: "FREQ=WEEKLY;COUNT=5") with
        {
            IsAllDay = allDay,
            StartUtc = allDay ? null : firstInstant,
            EndUtc = allDay ? null : firstInstant.Plus(Duration.FromHours(1)),
            StartDate = allDay ? first : null,
            EndDateExclusive = allDay ? first.PlusDays(1) : null,
            Exceptions = [new CalendarEventExceptionInfo(Guid.NewGuid(), allDay ? null : removedInstant,
                false, !allDay && unchangedStart ? removedInstant : null, null, "Edited title", null, null, null,
                allDay ? first.PlusDays(28) : null, allDay && unchangedStart ? first.PlusDays(28) : null)],
        };
        var from = Instant.FromUtc(2026, 8, 31, 0, 0);
        var to = Instant.FromUtc(2026, 10, 1, 0, 0);

        var before = CalendarOccurrenceExpander.Expand([info], from, to,
            new Dictionary<Guid, string>(), NullLogger.Instance);
        before.Should().HaveCount(5);
        before.Last().Title.Should().Be("Edited title");

        var after = CalendarOccurrenceExpander.Expand([info with { RecurrenceRule = "FREQ=WEEKLY;COUNT=2" }],
            from, to, new Dictionary<Guid, string>(), NullLogger.Instance);

        after.Should().HaveCount(2);
        after.Should().OnlyContain(occurrence => occurrence.Title == info.Title);
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void Expand_EndOnlyExtension_OverlapsWindowAfterOriginalDuration(bool allDay, bool unchangedStart)
    {
        var date = new LocalDate(2026, 9, 1);
        var start = Instant.FromUtc(2026, 9, 1, 10, 0);
        var extendedEnd = Instant.FromUtc(2026, 9, 3, 11, 0);
        var info = BuildInfo(start: start, end: start.Plus(Duration.FromHours(1)),
            recurrenceRule: "FREQ=WEEKLY;COUNT=2") with
        {
            IsAllDay = allDay,
            StartUtc = allDay ? null : start,
            EndUtc = allDay ? null : start.Plus(Duration.FromHours(1)),
            StartDate = allDay ? date : null,
            EndDateExclusive = allDay ? date.PlusDays(1) : null,
            Exceptions = [new CalendarEventExceptionInfo(Guid.NewGuid(), allDay ? null : start,
                false, !allDay && unchangedStart ? start : null, allDay ? null : extendedEnd, null, null, null, null,
                allDay ? date : null, allDay && unchangedStart ? date : null, allDay ? date.PlusDays(3) : null)],
        };

        var result = CalendarOccurrenceExpander.Expand([info], Instant.FromUtc(2026, 9, 3, 10, 0),
            Instant.FromUtc(2026, 9, 3, 12, 0), new Dictionary<Guid, string>(), NullLogger.Instance)
            .Should().ContainSingle().Subject;

        if (allDay)
        {
            result.StartDate.Should().Be(date);
            result.EndDateExclusive.Should().Be(date.PlusDays(3));
        }
        else
        {
            result.OccurrenceStartUtc.Should().Be(start);
            result.OccurrenceEndUtc.Should().Be(extendedEnd);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(false, "FREQ=WEEKLY;COUNT=2", false)]
    [Xunit.InlineData(false, "FREQ=WEEKLY;COUNT=2", true)]
    [Xunit.InlineData(false, "FREQ=WEEKLY;UNTIL=20260908T100000Z", false)]
    [Xunit.InlineData(false, "FREQ=WEEKLY;UNTIL=20260908T100000Z", true)]
    [Xunit.InlineData(false, "FREQ=WEEKLY;BYDAY=WE;COUNT=5", false)]
    [Xunit.InlineData(false, "FREQ=WEEKLY;BYDAY=WE;COUNT=5", true)]
    [Xunit.InlineData(true, "FREQ=WEEKLY;COUNT=2", false)]
    [Xunit.InlineData(true, "FREQ=WEEKLY;COUNT=2", true)]
    [Xunit.InlineData(true, "FREQ=WEEKLY;UNTIL=20260908", false)]
    [Xunit.InlineData(true, "FREQ=WEEKLY;UNTIL=20260908", true)]
    [Xunit.InlineData(true, "FREQ=WEEKLY;BYDAY=WE;COUNT=5", false)]
    [Xunit.InlineData(true, "FREQ=WEEKLY;BYDAY=WE;COUNT=5", true)]
    public void Expand_RecurrenceEdit_DropsEndExtensionsOfRemovedOccurrences(
        bool allDay, string changedRule, bool unchangedStart)
    {
        var firstDate = new LocalDate(2026, 9, 1);
        var first = Instant.FromUtc(2026, 9, 1, 10, 0);
        var removedDate = firstDate.PlusDays(28);
        var removed = first.Plus(Duration.FromDays(28));
        var info = BuildInfo(start: first, end: first.Plus(Duration.FromHours(1)),
            recurrenceRule: "FREQ=WEEKLY;COUNT=5") with
        {
            IsAllDay = allDay,
            StartUtc = allDay ? null : first,
            EndUtc = allDay ? null : first.Plus(Duration.FromHours(1)),
            StartDate = allDay ? firstDate : null,
            EndDateExclusive = allDay ? firstDate.PlusDays(1) : null,
            Exceptions = [new CalendarEventExceptionInfo(Guid.NewGuid(), allDay ? null : removed,
                false, !allDay && unchangedStart ? removed : null, allDay ? null : removed.Plus(Duration.FromDays(2)),
                null, null, null, null, allDay ? removedDate : null,
                allDay && unchangedStart ? removedDate : null, allDay ? removedDate.PlusDays(2) : null)],
        };
        var from = Instant.FromUtc(2026, 8, 31, 0, 0);
        var to = Instant.FromUtc(2026, 10, 2, 0, 0);

        var before = CalendarOccurrenceExpander.Expand([info], from, to,
            new Dictionary<Guid, string>(), NullLogger.Instance);
        before.Should().ContainSingle(o => allDay
            ? o.OriginalOccurrenceDate == removedDate : o.OriginalOccurrenceStartUtc == removed);

        var changed = info with { RecurrenceRule = changedRule };
        var expected = CalendarOccurrenceExpander.Expand([changed with { Exceptions = [] }], from, to,
            new Dictionary<Guid, string>(), NullLogger.Instance);
        expected.Should().NotContain(o => allDay
            ? o.OriginalOccurrenceDate == removedDate : o.OriginalOccurrenceStartUtc == removed);

        var after = CalendarOccurrenceExpander.Expand([changed], from, to,
            new Dictionary<Guid, string>(), NullLogger.Instance);

        after.Should().BeEquivalentTo(expected);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Expand_RemovedOccurrenceWithMovedStart_SurvivesOutsideSeriesWindow(bool allDay)
    {
        var firstDate = new LocalDate(2026, 9, 1);
        var first = Instant.FromUtc(2026, 9, 1, 10, 0);
        var removedDate = firstDate.PlusDays(28);
        var removed = first.Plus(Duration.FromDays(28));
        var movedDate = new LocalDate(2026, 10, 5);
        var moved = Instant.FromUtc(2026, 10, 5, 10, 0);
        var info = BuildInfo(start: first, end: first.Plus(Duration.FromHours(1)),
            recurrenceRule: "FREQ=WEEKLY;COUNT=2") with
        {
            IsAllDay = allDay,
            StartUtc = allDay ? null : first,
            EndUtc = allDay ? null : first.Plus(Duration.FromHours(1)),
            StartDate = allDay ? firstDate : null,
            EndDateExclusive = allDay ? firstDate.PlusDays(1) : null,
            Exceptions = [new CalendarEventExceptionInfo(Guid.NewGuid(), allDay ? null : removed,
                false, allDay ? null : moved, null, null, null, null, null,
                allDay ? removedDate : null, allDay ? movedDate : null)],
        };

        var result = CalendarOccurrenceExpander.Expand([info], Instant.FromUtc(2026, 10, 5, 0, 0),
            Instant.FromUtc(2026, 10, 7, 0, 0), new Dictionary<Guid, string>(), NullLogger.Instance)
            .Should().ContainSingle().Subject;

        if (allDay)
        {
            result.StartDate.Should().Be(movedDate);
            result.EndDateExclusive.Should().Be(movedDate.PlusDays(1));
            result.OriginalOccurrenceDate.Should().Be(removedDate);
        }
        else
        {
            result.OccurrenceStartUtc.Should().Be(moved);
            result.OccurrenceEndUtc.Should().Be(moved.Plus(Duration.FromHours(1)));
            result.OriginalOccurrenceStartUtc.Should().Be(removed);
        }
    }

    private static CalendarEventInfo BuildInfo(
        Guid? id = null,
        Guid? teamId = null,
        string title = "Test event",
        Instant? start = null,
        Instant? end = null,
        string? recurrenceRule = null,
        IReadOnlyList<CalendarEventExceptionInfo>? exceptions = null) => new(
            Id: id ?? Guid.NewGuid(),
            Title: title,
            Description: null,
            Location: null,
            LocationUrl: null,
            OwningTeamId: teamId ?? Guid.NewGuid(),
            StartUtc: start ?? Instant.FromUtc(2026, 6, 1, 10, 0),
            EndUtc: end ?? Instant.FromUtc(2026, 6, 1, 11, 0),
            IsAllDay: false,
            RecurrenceRule: recurrenceRule,
            RecurrenceTimezone: recurrenceRule is null ? null : "UTC",
            RecurrenceUntilUtc: null,
            CreatedByUserId: Guid.NewGuid(),
            CreatedAt: Instant.FromUtc(2026, 5, 1, 0, 0),
            UpdatedAt: Instant.FromUtc(2026, 5, 1, 0, 0),
            Exceptions: exceptions ?? []);
}
