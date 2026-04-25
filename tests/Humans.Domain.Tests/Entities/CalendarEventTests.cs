using AwesomeAssertions;
using NodaTime;
using Humans.Domain.Entities;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class CalendarEventTests
{
    private static readonly Instant Jan1 = Instant.FromUtc(2026, 1, 1, 10, 0);
    private static readonly Instant Jan1End = Instant.FromUtc(2026, 1, 1, 11, 0);

    [HumansFact]
    public void TimedEvent_requires_EndUtc()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            StartUtc = Jan1,
            EndUtc = null,
            IsAllDay = false,
            OwningTeamId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1
        };

        var errors = ev.Validate();

        errors.Should().ContainMatch("*EndUtc*");
    }

    [HumansFact]
    public void AllDayEvent_allows_null_EndUtc()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            StartUtc = Jan1,
            EndUtc = null,
            IsAllDay = true,
            OwningTeamId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1
        };

        var errors = ev.Validate();

        errors.Should().BeEmpty();
    }

    [HumansFact]
    public void RecurrenceRule_without_timezone_is_invalid()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            StartUtc = Jan1,
            EndUtc = Jan1End,
            IsAllDay = false,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
            RecurrenceTimezone = null,
            OwningTeamId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1
        };

        var errors = ev.Validate();

        errors.Should().ContainMatch("*RecurrenceTimezone*");
    }

    [HumansFact]
    public void RecurrenceTimezone_without_rule_is_invalid()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            StartUtc = Jan1,
            EndUtc = Jan1End,
            IsAllDay = false,
            RecurrenceRule = null,
            RecurrenceTimezone = "Europe/Madrid",
            OwningTeamId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1
        };

        var errors = ev.Validate();

        errors.Should().ContainMatch("*RecurrenceTimezone*");
    }

    [HumansFact]
    public void StartAfterEnd_is_invalid()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            StartUtc = Jan1End,
            EndUtc = Jan1,
            IsAllDay = false,
            OwningTeamId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1
        };

        var errors = ev.Validate();

        errors.Should().ContainMatch("*StartUtc*");
    }
}
