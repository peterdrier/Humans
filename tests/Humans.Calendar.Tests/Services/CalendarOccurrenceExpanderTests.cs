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
            Id = Guid.NewGuid(), Title = "All day", IsAllDay = true,
            StartUtc = day.AtStartOfDayInZone(zone).ToInstant(),
            EndUtc = day.PlusDays(1).AtStartOfDayInZone(zone).ToInstant(),
            RecurrenceRule = "FREQ=DAILY;COUNT=5", RecurrenceTimezone = zone.Id,
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
            IsAllDay = true, StartUtc = null, EndUtc = null, RecurrenceTimezone = null,
            StartDate = first, EndDateExclusive = first.PlusDays(2),
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
            IsAllDay = true, StartUtc = null, EndUtc = null, RecurrenceTimezone = null,
            StartDate = new LocalDate(2026, 3, 28), EndDateExclusive = new LocalDate(2026, 3, 30),
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
            IsAllDay = true, StartUtc = null, EndUtc = null, RecurrenceTimezone = null,
            StartDate = date, EndDateExclusive = date.PlusDays(2), RecurrenceUntilDate = date.PlusDays(3),
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
