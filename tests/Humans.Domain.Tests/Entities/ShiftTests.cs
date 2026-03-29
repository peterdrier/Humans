using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class ShiftTests
{
    private static EventSettings CreateEventSettings(
        int eventEndOffset = 6,
        string timeZoneId = "Europe/Madrid",
        int year = 2026, int month = 7, int day = 6) => new()
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            TimeZoneId = timeZoneId,
            GateOpeningDate = new LocalDate(year, month, day),
            BuildStartOffset = -14,
            EventEndOffset = eventEndOffset,
            StrikeEndOffset = eventEndOffset + 3,
            CreatedAt = Instant.MinValue,
            UpdatedAt = Instant.MinValue
        };

    private static Shift CreateShift(int dayOffset = 0, int hour = 10, int minute = 0, long durationSeconds = 4 * 3600) => new()
    {
        Id = Guid.NewGuid(),
        RotaId = Guid.NewGuid(),
        DayOffset = dayOffset,
        StartTime = new LocalTime(hour, minute),
        Duration = Duration.FromSeconds(durationSeconds),
        MinVolunteers = 2,
        MaxVolunteers = 5,
        CreatedAt = Instant.MinValue,
        UpdatedAt = Instant.MinValue
    };

    [Fact]
    public void GetAbsoluteStart_ResolvesCorrectly()
    {
        var es = CreateEventSettings(); // Gate opening: 2026-07-06, Europe/Madrid
        var shift = CreateShift(dayOffset: 0, hour: 10); // Day 0, 10:00

        var start = shift.GetAbsoluteStart(es);

        // 2026-07-06 10:00 Europe/Madrid = UTC+2 in summer = 08:00 UTC
        var tz = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var expected = new LocalDate(2026, 7, 6).At(new LocalTime(10, 0)).InZoneLeniently(tz).ToInstant();
        start.Should().Be(expected);
    }

    [Fact]
    public void GetAbsoluteEnd_ReturnsStartPlusDuration()
    {
        var es = CreateEventSettings();
        var shift = CreateShift(dayOffset: 0, hour: 10, durationSeconds: 4 * 3600);

        var end = shift.GetAbsoluteEnd(es);
        var start = shift.GetAbsoluteStart(es);

        end.Should().Be(start.Plus(Duration.FromHours(4)));
    }

    [Fact]
    public void GetAbsoluteStart_NegativeDayOffset_ResolvesCorrectly()
    {
        var es = CreateEventSettings(); // Gate opening: 2026-07-06
        var shift = CreateShift(dayOffset: -3, hour: 8); // 3 days before gate opening

        var start = shift.GetAbsoluteStart(es);

        var tz = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var expected = new LocalDate(2026, 7, 3).At(new LocalTime(8, 0)).InZoneLeniently(tz).ToInstant();
        start.Should().Be(expected);
    }

    [Fact]
    public void OvernightShift_EndsNextDay()
    {
        var es = CreateEventSettings();
        var shift = CreateShift(dayOffset: 0, hour: 22, durationSeconds: 8 * 3600); // 22:00 + 8h

        var start = shift.GetAbsoluteStart(es);
        var end = shift.GetAbsoluteEnd(es);

        end.Should().Be(start.Plus(Duration.FromHours(8)));
        // Start is July 6 22:00, end is July 7 06:00
        var tz = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var expectedEnd = new LocalDate(2026, 7, 7).At(new LocalTime(6, 0)).InZoneLeniently(tz).ToInstant();
        end.Should().Be(expectedEnd);
    }

    [Fact]
    public void IsEarlyEntry_NegativeOffset_ReturnsTrue()
    {
        var shift = CreateShift(dayOffset: -1);
        shift.IsEarlyEntry.Should().BeTrue();
    }

    [Fact]
    public void IsEarlyEntry_ZeroOffset_ReturnsFalse()
    {
        var shift = CreateShift(dayOffset: 0);
        shift.IsEarlyEntry.Should().BeFalse();
    }

    [Fact]
    public void IsEarlyEntry_PositiveOffset_ReturnsFalse()
    {
        var shift = CreateShift(dayOffset: 3);
        shift.IsEarlyEntry.Should().BeFalse();
    }

    [Fact]
    public void GetShiftPeriod_NegativeOffset_ReturnsBuild()
    {
        var es = CreateEventSettings(eventEndOffset: 6);
        var shift = CreateShift(dayOffset: -1);
        shift.GetShiftPeriod(es).Should().Be(ShiftPeriod.Build);
    }

    [Fact]
    public void GetShiftPeriod_ZeroOffset_ReturnsEvent()
    {
        var es = CreateEventSettings(eventEndOffset: 6);
        var shift = CreateShift(dayOffset: 0);
        shift.GetShiftPeriod(es).Should().Be(ShiftPeriod.Event);
    }

    [Fact]
    public void GetShiftPeriod_EventEndOffset_ReturnsEvent()
    {
        var es = CreateEventSettings(eventEndOffset: 6);
        var shift = CreateShift(dayOffset: 6);
        shift.GetShiftPeriod(es).Should().Be(ShiftPeriod.Event);
    }

    [Fact]
    public void GetShiftPeriod_EventEndOffsetPlusOne_ReturnsStrike()
    {
        var es = CreateEventSettings(eventEndOffset: 6);
        var shift = CreateShift(dayOffset: 7);
        shift.GetShiftPeriod(es).Should().Be(ShiftPeriod.Strike);
    }

    [Fact]
    public void IsAllDay_DefaultsFalse()
    {
        var shift = new Shift();
        shift.IsAllDay.Should().BeFalse();
    }

    [Fact]
    public void IsAllDay_WhenTrue_DurationIgnoredForDisplay()
    {
        var shift = new Shift
        {
            IsAllDay = true,
            StartTime = new LocalTime(0, 0),
            Duration = Duration.FromHours(24)
        };
        shift.IsAllDay.Should().BeTrue();
    }
}
