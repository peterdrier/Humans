using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Calendar;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NodaTime;
using NodaTime.Testing;
using NodaTime.TimeZones;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the write-time validation helpers that support PR #562.
///
/// <para>Covers:</para>
/// <list type="bullet">
///   <item><see cref="CalendarService.ValidateRecurrenceRule"/> — rejects malformed
///     RRULEs so they cannot persist and blow up later occurrence expansion.</item>
///   <item><see cref="CalendarService.ValidateTimezone"/> — rejects unknown timezone
///     IDs at the service boundary so non-controller callers can't slip past the
///     web-layer guard and crash inside occurrence expansion.</item>
///   <item>NodaTime Tzdb contract — <c>GetZoneOrNull</c> returns null for unknown IDs
///     (which is what the CalendarController timezone guard depends on) and the indexer
///     throws the way the original bug reported.</item>
/// </list>
/// </summary>
public class CalendarServiceValidationTests
{
    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU;COUNT=4")]
    [InlineData("FREQ=WEEKLY;UNTIL=20240201T000000Z")]
    public void ValidateRecurrenceRule_valid_input_does_not_throw(string? rrule)
    {
        var act = () => CalendarService.ValidateRecurrenceRule(rrule);
        act.Should().NotThrow();
    }

    [HumansTheory]
    [InlineData("FREQ=NOT_A_REAL_FREQ")]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    public void ValidateRecurrenceRule_malformed_input_throws_ValidationException(string rrule)
    {
        var act = () => CalendarService.ValidateRecurrenceRule(rrule);
        act.Should().Throw<ValidationException>()
            .WithMessage("*Recurrence rule is malformed*");
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Europe/Madrid")]
    [InlineData("UTC")]
    public void ValidateTimezone_valid_input_does_not_throw(string? tz)
    {
        var act = () => CalendarService.ValidateTimezone(tz);
        act.Should().NotThrow();
    }

    [HumansTheory]
    [InlineData("Europe/Madird")]
    [InlineData("Not/A/Real/Zone")]
    public void ValidateTimezone_unknown_input_throws_ValidationException(string tz)
    {
        var act = () => CalendarService.ValidateTimezone(tz);
        act.Should().Throw<ValidationException>()
            .WithMessage("*Recurrence timezone is unknown*");
    }

    // The three tests below document the NodaTime Tzdb contract the CalendarController
    // timezone guard depends on — GetZoneOrNull returns null for unknown IDs, while the
    // indexer throws (which was the original #562 bug). Pin the contract so a NodaTime
    // upgrade that changes either behavior is caught at test time.

    [HumansTheory]
    [InlineData("Europe/Madrid")]
    [InlineData("UTC")]
    [InlineData("America/Los_Angeles")]
    public void Tzdb_GetZoneOrNull_returns_zone_for_known_id(string id)
    {
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(id).Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("Europe/Madird")]       // typo'd Madrid, original bug example
    [InlineData("Not/A/Real/Zone")]
    [InlineData("")]
    public void Tzdb_GetZoneOrNull_returns_null_for_unknown_id(string id)
    {
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(id).Should().BeNull();
    }

    [HumansFact]
    public void Tzdb_indexer_throws_for_unknown_id()
    {
        // The indexer is what the pre-fix controller used; it throws, which surfaced
        // as a 500 to the user on submit. The fix swaps this for GetZoneOrNull.
        var act = () => DateTimeZoneProviders.Tzdb["Europe/Madird"];
        act.Should().Throw<DateTimeZoneNotFoundException>();
    }

    [HumansFact]
    public async Task CreateEventWithResultAsync_returns_validation_member_for_malformed_recurrence()
    {
        var repo = Substitute.For<ICalendarRepository>();
        var service = BuildService(repo);
        var dto = new CreateCalendarEventDto(
            "Planning",
            Description: null,
            Location: null,
            LocationUrl: null,
            OwningTeamId: Guid.NewGuid(),
            StartUtc: Instant.FromUtc(2026, 5, 15, 17, 0),
            EndUtc: Instant.FromUtc(2026, 5, 15, 18, 0),
            IsAllDay: false,
            RecurrenceRule: "FREQ=NOT_A_REAL_FREQ",
            RecurrenceTimezone: "Europe/Madrid");

        var result = await service.CreateEventWithResultAsync(dto, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.ValidationMemberName.Should().Be(nameof(CreateCalendarEventDto.RecurrenceRule));
        result.ErrorMessage.Should().Contain("Recurrence rule is malformed");
        await repo.DidNotReceive().AddAsync(Arg.Any<Humans.Domain.Entities.CalendarEvent>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateEventWithResultAsync_returns_validation_member_for_unknown_timezone()
    {
        var service = BuildService(Substitute.For<ICalendarRepository>());
        var dto = new UpdateCalendarEventDto(
            "Planning",
            Description: null,
            Location: null,
            LocationUrl: null,
            OwningTeamId: Guid.NewGuid(),
            StartUtc: Instant.FromUtc(2026, 5, 15, 17, 0),
            EndUtc: Instant.FromUtc(2026, 5, 15, 18, 0),
            IsAllDay: false,
            RecurrenceRule: "FREQ=DAILY",
            RecurrenceTimezone: "Europe/Madird");

        var result = await service.UpdateEventWithResultAsync(Guid.NewGuid(), dto, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.ValidationMemberName.Should().Be(nameof(CreateCalendarEventDto.RecurrenceTimezone));
        result.ErrorMessage.Should().Contain("Recurrence timezone is unknown");
    }

    private static CalendarService BuildService(ICalendarRepository repo)
    {
        return new CalendarService(
            repo,
            Substitute.For<ITeamService>(),
            new MemoryCache(new MemoryCacheOptions()),
            new FakeClock(Instant.FromUtc(2026, 5, 15, 12, 0)),
            Substitute.For<IAuditLogService>(),
            NullLogger<CalendarService>.Instance);
    }
}
