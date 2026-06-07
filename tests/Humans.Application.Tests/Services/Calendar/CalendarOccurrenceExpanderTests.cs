using AwesomeAssertions;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Services.Calendar;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Humans.Application.Tests.Services.Calendar;

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
