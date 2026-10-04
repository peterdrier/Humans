using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Calendar.Services.Dtos;
using Humans.AuditLog.Contracts;
using Humans.Calendar.Data;
using Humans.Teams.Contracts;
using Humans.Calendar.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NodaTime;
using NodaTime.Testing;
using NodaTime.TimeZones;
using Xunit;

namespace Humans.Calendar.Tests.Services;

/// <summary>
/// Unit tests for the write-time validation helpers that support nobodies-collective/Humans#562.
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
    [InlineData("FREQ=DAILY;COUNT=3", false)]
    [InlineData("FREQ=DAILY;UNTIL=20260603T100000Z", false)]
    [InlineData("FREQ=DAILY;COUNT=3", true)]
    [InlineData("FREQ=DAILY;UNTIL=20260603T100000Z", true)]
    public async Task EventWithResultAsync_InvalidDuration_is_a_validation_failure(string rule, bool update)
    {
        var repo = Substitute.For<ICalendarRepository>();
        var service = BuildService(repo);
        var start = Instant.FromUtc(2026, 6, 1, 10, 0);

        var eventId = Guid.NewGuid();
        repo.UpdateAsync(eventId, Arg.Any<Action<Humans.Calendar.Domain.CalendarEvent>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<Action<Humans.Calendar.Domain.CalendarEvent>>(1)(new() { Id = eventId });
                return true;
            });
        var dto = new CreateCalendarEventDto(
            "Invalid duration", null, null, null, Guid.NewGuid(),
            start, start.Minus(Duration.FromHours(1)), false, rule, "UTC");
        var result = update
            ? await service.UpdateEventWithResultAsync(eventId, dto, Guid.NewGuid(), TestContext.Current.CancellationToken)
            : await service.CreateEventWithResultAsync(dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be("Calendar_InvalidTimedEvent");
        await repo.DidNotReceive().AddAsync(
            Arg.Any<Humans.Calendar.Domain.CalendarEvent>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateEventWithResultAsync_UsesTheRepositoryMissingOutcome(bool dependencyFailure)
    {
        var repo = Substitute.For<ICalendarRepository>();
        var audit = Substitute.For<IAuditLogService>();
        var service = BuildService(repo, audit);
        var id = Guid.NewGuid();
        repo.UpdateAsync(id, Arg.Any<Action<Humans.Calendar.Domain.CalendarEvent>>(), Arg.Any<CancellationToken>())
            .Returns(_ => dependencyFailure
                ? Task.FromException<bool>(new InvalidOperationException("Required property not found in persistence metadata."))
                : Task.FromResult(false));
        var start = Instant.FromUtc(2026, 6, 1, 10, 0);
        var dto = new CreateCalendarEventDto("Event", null, null, null, Guid.NewGuid(),
            start, start + Duration.FromHours(1), false, null, "UTC");

        var result = await service.UpdateEventWithResultAsync(id, dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.NotFound.Should().Be(!dependencyFailure, "only a missing event row warrants the missing result");
        result.ErrorMessage.Should().Be(dependencyFailure ? "Calendar_SaveFailed" : "Calendar event not found.");
        audit.ReceivedCalls().Should().BeEmpty();
    }

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
    // indexer throws (the original nobodies-collective/Humans#562 bug). Pin the contract so a NodaTime
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

    [HumansTheory]
    [InlineData("FREQ=NOT_A_REAL_FREQ", false)]
    [InlineData("FREQ=TIMEZONE", false)]
    [InlineData("FREQ=NOT_A_REAL_FREQ", true)]
    [InlineData("FREQ=TIMEZONE", true)]
    public async Task EventWithResultAsync_returns_validation_member_for_malformed_recurrence(string rule, bool update)
    {
        var repo = Substitute.For<ICalendarRepository>();
        var logger = Substitute.For<ILogger<CalendarService>>();
        var service = new CalendarService(repo,
            new FakeClock(Instant.FromUtc(2026, 5, 15, 12, 0)),
            Substitute.For<IAuditLogService>(), logger);
        var dto = new CreateCalendarEventDto(
            "Planning",
            Description: null,
            Location: null,
            LocationUrl: null,
            OwningTeamId: Guid.NewGuid(),
            StartUtc: Instant.FromUtc(2026, 5, 15, 17, 0),
            EndUtc: Instant.FromUtc(2026, 5, 15, 18, 0),
            IsAllDay: false,
            RecurrenceRule: rule,
            RecurrenceTimezone: "Europe/Madrid");

        var result = update
            ? await service.UpdateEventWithResultAsync(Guid.NewGuid(), dto, Guid.NewGuid(), TestContext.Current.CancellationToken)
            : await service.CreateEventWithResultAsync(dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        var log = logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).GetArguments();
        log[0].Should().Be(LogLevel.Warning);
        log[3].Should().BeNull("invalid user input should not log an exception stack");
        result.ValidationMemberName.Should().Be(nameof(CreateCalendarEventDto.RecurrenceRule));
        result.ErrorMessage.Should().Be("Calendar_InvalidTimedRecurrence");
        await repo.DidNotReceive().AddAsync(Arg.Any<Humans.Calendar.Domain.CalendarEvent>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateEventWithResultAsync_returns_validation_member_for_unknown_timezone()
    {
        var repo = Substitute.For<ICalendarRepository>();
        var logger = Substitute.For<ILogger<CalendarService>>();
        var service = new CalendarService(repo,
            new FakeClock(Instant.FromUtc(2026, 5, 15, 12, 0)),
            Substitute.For<IAuditLogService>(), logger);
        var dto = new CreateCalendarEventDto(
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

        var result = await service.UpdateEventWithResultAsync(Guid.NewGuid(), dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        var log = logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).GetArguments();
        log[0].Should().Be(LogLevel.Warning);
        log[3].Should().BeNull("invalid user input should not log an exception stack");
        result.ValidationMemberName.Should().Be(nameof(CreateCalendarEventDto.RecurrenceTimezone));
        result.ErrorMessage.Should().Be("Calendar_UnknownTimezone");
    }

    [HumansFact]
    public async Task CreateEventWithResultAsync_COUNT_rule_persists_the_last_occurrence_end()
    {
        var repo = Substitute.For<ICalendarRepository>();
        var service = BuildService(repo);
        var start = Instant.FromUtc(2026, 6, 1, 10, 0);

        var result = await service.CreateEventWithResultAsync(
            new CreateCalendarEventDto(
                "Three daily events", null, null, null, Guid.NewGuid(),
                start, start + Duration.FromHours(1), false,
                "FREQ=DAILY;COUNT=3", "UTC"),
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert success explicitly: the result form swallows exceptions, so a rejected create would otherwise reach the AddAsync
        // assertion below as a silent zero-call.
        result.Succeeded.Should().BeTrue(result.ErrorMessage);

        await repo.Received(1).AddAsync(
            Arg.Is<Humans.Calendar.Domain.CalendarEvent>(e =>
                e.RecurrenceUntilUtc == Instant.FromUtc(2026, 6, 3, 11, 0)),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("FREQ=DAILY;UNTIL=20260603T100000Z", false)]
    [InlineData("FREQ=DAILY;UNTIL=20260603T100000z", false)]
    [InlineData("freq=daily;until=20260603t100000z", false)]
    [InlineData("FREQ=DAILY;UNTIL=20260603T100000Z", true)]
    [InlineData("FREQ=DAILY;UNTIL=20260603T100000z", true)]
    [InlineData("freq=daily;until=20260603t100000z", true)]
    public async Task EventWithResultAsync_UTC_until_is_independent_of_case_and_server_timezone(string rule, bool update)
    {
        var validate = () => CalendarService.ValidateRecurrenceRule(rule);
        validate.Should().NotThrow();
        var repo = Substitute.For<ICalendarRepository>();
        var service = BuildService(repo);
        var start = Instant.FromUtc(2026, 6, 1, 10, 0);
        var ev = new Humans.Calendar.Domain.CalendarEvent { Id = Guid.NewGuid() };
        repo.UpdateAsync(ev.Id, Arg.Any<Action<Humans.Calendar.Domain.CalendarEvent>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<Action<Humans.Calendar.Domain.CalendarEvent>>(1)(ev);
                return true;
            });
        var dto = new CreateCalendarEventDto(
            "UTC-bounded daily events", null, null, null, Guid.NewGuid(),
            start, start + Duration.FromHours(1), false, rule, "Europe/Madrid");

        var result = update
            ? await service.UpdateEventWithResultAsync(ev.Id, dto, Guid.NewGuid(), TestContext.Current.CancellationToken)
            : await service.CreateEventWithResultAsync(dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        var expected = Instant.FromUtc(2026, 6, 3, 10, 0);
        if (update)
            ev.RecurrenceUntilUtc.Should().Be(expected);
        else
            await repo.Received(1).AddAsync(
                Arg.Is<Humans.Calendar.Domain.CalendarEvent>(e => e.RecurrenceUntilUtc == expected),
                Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateEventWithResultAsync_DATE_until_persists_the_end_of_that_local_day()
    {
        var repo = Substitute.For<ICalendarRepository>();
        var service = BuildService(repo);
        var start = Instant.FromUtc(2026, 6, 1, 10, 0);

        var result = await service.CreateEventWithResultAsync(
            new CreateCalendarEventDto(
                "Madrid daily events", null, null, null, Guid.NewGuid(),
                start, start + Duration.FromHours(1), false,
                "FREQ=DAILY;UNTIL=20260603", "Europe/Madrid"),
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);

        await repo.Received(1).AddAsync(
            Arg.Is<Humans.Calendar.Domain.CalendarEvent>(e =>
                e.RecurrenceUntilUtc == Instant.FromUtc(2026, 6, 3, 22, 0)),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("20260329T023000", "Europe/Madrid", 2026, 3, 29, 1, 30)]
    [InlineData("20261025T023000", "Europe/Madrid", 2026, 10, 25, 0, 30)]
    [InlineData("20111229", "Pacific/Apia", 2011, 12, 30, 10, 0)]
    public async Task CreateEventWithResultAsync_LocalUntil_uses_occurrence_timezone_resolution(
        string until, string timezone, int year, int month, int day, int hour, int minute)
    {
        var repo = Substitute.For<ICalendarRepository>();
        var service = BuildService(repo);
        var expectedUntil = Instant.FromUtc(year, month, day, hour, minute);
        var start = expectedUntil.Minus(Duration.FromDays(3));

        var result = await service.CreateEventWithResultAsync(
            new CreateCalendarEventDto(
                "Timezone-transition recurrence", null, null, null, Guid.NewGuid(),
                start, start + Duration.FromHours(1), false,
                $"FREQ=DAILY;UNTIL={until}", timezone),
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        await repo.Received(1).AddAsync(
            Arg.Is<Humans.Calendar.Domain.CalendarEvent>(e => e.RecurrenceUntilUtc == expectedUntil),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Audit-best-effort invariant
    // ==========================================================================
    //
    // Audit logging is best-effort after the DB write has committed. A
    // post-write audit failure must not propagate or turn a successful
    // mutation result into a failed one.

    [HumansFact]
    public async Task CreateEventWithResultAsync_AuditThrowsAfterWrite_StillReturnsSuccess()
    {
        var repo = Substitute.For<ICalendarRepository>();
        var audit = Substitute.For<IAuditLogService>();
        audit.LogAsync(
                Arg.Any<AuditAction>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("audit log connection lost")));

        var logger = Substitute.For<ILogger<CalendarService>>();
        var service = BuildService(repo, audit, logger);
        var dto = new CreateCalendarEventDto(
            "Audit-fails-after-create", null, null, null,
            OwningTeamId: Guid.NewGuid(),
            StartUtc: Instant.FromUtc(2026, 5, 15, 17, 0),
            EndUtc: Instant.FromUtc(2026, 5, 15, 18, 0),
            IsAllDay: false,
            RecurrenceRule: null,
            RecurrenceTimezone: null);

        var result = await service.CreateEventWithResultAsync(dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            because: "the DB write committed; audit failure is best-effort and must not void the result");
        result.Event.Should().NotBeNull();
        result.Event!.Title.Should().Be("Audit-fails-after-create");
        await repo.Received(1).AddAsync(Arg.Any<Humans.Calendar.Domain.CalendarEvent>(), Arg.Any<CancellationToken>());
        AssertLoggedCritical(logger);
    }

    [HumansFact]
    public async Task CreateEventWithResultAsync_AddCancellation_Propagates()
    {
        var repo = Substitute.For<ICalendarRepository>();
        using var cancellation = new CancellationTokenSource();
        repo.AddAsync(Arg.Any<Humans.Calendar.Domain.CalendarEvent>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await cancellation.CancelAsync();
                await Task.FromCanceled(cancellation.Token);
            });

        var service = BuildService(repo);
        var dto = new CreateCalendarEventDto(
            "Canceled create", null, null, null,
            OwningTeamId: Guid.NewGuid(),
            StartUtc: Instant.FromUtc(2026, 5, 15, 17, 0),
            EndUtc: Instant.FromUtc(2026, 5, 15, 18, 0),
            IsAllDay: false,
            RecurrenceRule: null,
            RecurrenceTimezone: null);

        var act = () => service.CreateEventWithResultAsync(dto, Guid.NewGuid(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task UpdateEventWithResultAsync_AuditThrowsAfterWrite_StillReturnsSuccess()
    {
        var repo = Substitute.For<ICalendarRepository>();
        var eventId = Guid.NewGuid();

        // Repo.UpdateAsync invokes the apply callback then returns true; the
        // service captures the mutated event in a closure and returns it.
        repo.UpdateAsync(eventId, Arg.Any<Action<Humans.Calendar.Domain.CalendarEvent>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var apply = callInfo.ArgAt<Action<Humans.Calendar.Domain.CalendarEvent>>(1);
                apply(new Humans.Calendar.Domain.CalendarEvent
                {
                    Id = eventId,
                    Title = "ignored — apply overwrites",
                    OwningTeamId = Guid.NewGuid(),
                    StartUtc = Instant.FromUtc(2026, 5, 15, 17, 0),
                    EndUtc = Instant.FromUtc(2026, 5, 15, 18, 0),
                    IsAllDay = false,
                    CreatedByUserId = Guid.NewGuid(),
                    CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                });
                return Task.FromResult(true);
            });

        var audit = Substitute.For<IAuditLogService>();
        audit.LogAsync(
                Arg.Any<AuditAction>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("audit log connection lost")));

        var service = BuildService(repo, audit);
        var dto = new CreateCalendarEventDto(
            "Audit-fails-after-update", null, null, null,
            OwningTeamId: Guid.NewGuid(),
            StartUtc: Instant.FromUtc(2026, 5, 15, 17, 0),
            EndUtc: Instant.FromUtc(2026, 5, 15, 18, 0),
            IsAllDay: false,
            RecurrenceRule: null,
            RecurrenceTimezone: null);

        var result = await service.UpdateEventWithResultAsync(eventId, dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            because: "the DB write committed; audit failure is best-effort and must not void the result");
        result.Event.Should().NotBeNull();
        result.Event!.Title.Should().Be("Audit-fails-after-update");
    }

    [HumansFact]
    public async Task DeleteEventAsync_AuditThrowsAfterWrite_DoesNotThrow()
    {
        var repo = Substitute.For<ICalendarRepository>();
        var eventId = Guid.NewGuid();
        repo.SoftDeleteAsync(eventId, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), "Deleted event"));

        var audit = Substitute.For<IAuditLogService>();
        audit.LogAsync(
                Arg.Any<AuditAction>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("audit log connection lost")));

        var service = BuildService(repo, audit);

        // Void overload — re-throwing audit would also skip the decorator's
        // post-delete invalidation, leaving the soft-deleted event in cache.
        var act = async () => await service.DeleteEventAsync(eventId, Guid.NewGuid(), TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task CancelOccurrenceAsync_AuditThrowsAfterWrite_DoesNotThrow()
    {
        var repo = Substitute.For<ICalendarRepository>();
        repo.GetEventByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new Humans.Calendar.Domain.CalendarEvent
        {
            Id = Guid.NewGuid(),
            StartUtc = Instant.FromUtc(2026, 6, 1, 10, 0),
            RecurrenceRule = "FREQ=DAILY",
            RecurrenceTimezone = "UTC",
        });
        var audit = Substitute.For<IAuditLogService>();
        audit.LogAsync(
                Arg.Any<AuditAction>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("audit log connection lost")));

        var logger = Substitute.For<ILogger<CalendarService>>();
        var service = BuildService(repo, audit, logger);

        var act = async () => await service.CancelOccurrenceAsync(
            Guid.NewGuid(), Instant.FromUtc(2026, 6, 1, 10, 0), Guid.NewGuid(), TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync();
        AssertLoggedCritical(logger);
    }

    [HumansTheory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "Required property missing in persistence metadata.")]
    [InlineData(true, "Required property missing in persistence metadata.")]
    [InlineData(false, "Calendar_CannotChangeEventType")]
    [InlineData(true, "Calendar_CannotChangeEventType")]
    public async Task EventMutation_UnexpectedWriteFailureReturnsLocalizedSaveKey(bool update, string? invalidOperationMessage)
    {
        Exception failure = invalidOperationMessage is null
            ? new IOException("database unavailable")
            : new InvalidOperationException(invalidOperationMessage);
        var repo = Substitute.For<ICalendarRepository>();
        repo.AddAsync(Arg.Any<Humans.Calendar.Domain.CalendarEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));
        repo.UpdateAsync(Arg.Any<Guid>(), Arg.Any<Action<Humans.Calendar.Domain.CalendarEvent>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(failure));
        var service = BuildService(repo);
        var dto = new CreateCalendarEventDto(
            "Event", null, null, null, Guid.NewGuid(),
            Instant.FromUtc(2026, 5, 15, 17, 0), Instant.FromUtc(2026, 5, 15, 18, 0),
            false, null, null);

        var result = update
            ? await service.UpdateEventWithResultAsync(Guid.NewGuid(), dto, Guid.NewGuid(), TestContext.Current.CancellationToken)
            : await service.CreateEventWithResultAsync(dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be("Calendar_SaveFailed");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventMutation_InvalidTimedEventReturnsLocalizedValidationKey(bool update)
    {
        var repo = Substitute.For<ICalendarRepository>();
        repo.UpdateAsync(Arg.Any<Guid>(), Arg.Any<Action<Humans.Calendar.Domain.CalendarEvent>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<Action<Humans.Calendar.Domain.CalendarEvent>>(1)(new Humans.Calendar.Domain.CalendarEvent());
                return true;
            });
        var service = BuildService(repo);
        var dto = new CreateCalendarEventDto(
            "", null, null, null, Guid.NewGuid(),
            Instant.FromUtc(2026, 5, 15, 17, 0), Instant.FromUtc(2026, 5, 15, 18, 0),
            false, null, null);

        var result = update
            ? await service.UpdateEventWithResultAsync(Guid.NewGuid(), dto, Guid.NewGuid(), TestContext.Current.CancellationToken)
            : await service.CreateEventWithResultAsync(dto, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ValidationMemberName.Should().BeNull();
        result.ErrorMessage.Should().Be("Calendar_InvalidTimedEvent");
        await repo.DidNotReceive().AddAsync(Arg.Any<Humans.Calendar.Domain.CalendarEvent>(), Arg.Any<CancellationToken>());
    }

    private static CalendarService BuildService(ICalendarRepository repo)
    {
        return BuildService(repo, Substitute.For<IAuditLogService>());
    }

    private static CalendarService BuildService(
        ICalendarRepository repo, IAuditLogService audit, ILogger<CalendarService>? logger = null)
    {
        return new CalendarService(
            repo,
            new FakeClock(Instant.FromUtc(2026, 5, 15, 12, 0)),
            audit,
            logger ?? NullLogger<CalendarService>.Instance);
    }

    // The failure is swallowed, so the Critical log carrying the audit exception is the only
    // trace it leaves (health.md invariant 5: never passes silently).
    private static void AssertLoggedCritical(ILogger<CalendarService> logger)
    {
        var log = logger.ReceivedCalls()
            .Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal))
            .GetArguments();
        log[0].Should().Be(LogLevel.Critical);
        log[3].Should().BeOfType<InvalidOperationException>();
    }
}
