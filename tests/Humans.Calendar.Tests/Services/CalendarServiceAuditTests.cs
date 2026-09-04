using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Calendar.Data;
using Humans.Calendar.Domain;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Calendar.Tests.Services;

/// <summary>
/// "The Board can see what happened" is a section invariant, and until these tests
/// existed every <c>audit.LogAsync</c> call in <see cref="CalendarService"/> could be
/// deleted with the whole suite still green. Each mutation writes one entry; the
/// event-level ones additionally carry the owning team as <c>relatedEntityId</c>, which
/// is what makes team-scoped audit filtering work, and the per-occurrence ones
/// deliberately do not.
/// </summary>
public class CalendarServiceAuditTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 15, 12, 0);
    private readonly ICalendarRepository _repo = Substitute.For<ICalendarRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    private CalendarService CreateSut() => new(
        _repo,
        Substitute.For<ITeamService>(),
        new FakeClock(Now),
        _audit,
        NullLogger<CalendarService>.Instance);

    [HumansFact]
    public async Task CreateEventAsync_WritesOneAuditEntry_RelatedToTheOwningTeam()
    {
        var teamId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var sut = CreateSut();

        var ev = await sut.CreateEventAsync(
            new CreateCalendarEventDto(
                "Planning", null, null, null, teamId,
                Instant.FromUtc(2026, 6, 1, 10, 0), Instant.FromUtc(2026, 6, 1, 11, 0),
                false, null, null),
            actor, TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.CalendarEventCreated, AuditEntityTypes.CalendarEvent, ev.Id,
            Arg.Any<string>(), actor,
            teamId, AuditEntityTypes.Team);
    }

    // Update was the mutation this suite originally missed, so its LogAsync call stayed
    // deletable with everything else pinned. The team it names is the one the update *sets*,
    // which is what keeps a moved event auditable under its new team rather than its old.
    [HumansFact]
    public async Task UpdateEventAsync_WritesOneAuditEntry_RelatedToTheOwningTeam()
    {
        var id = Guid.NewGuid();
        var newTeamId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.UpdateAsync(id, Arg.Any<Action<CalendarEvent>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Action<CalendarEvent>>()(new CalendarEvent
                {
                    Id = id,
                    CreatedByUserId = actor,
                    CreatedAt = Now,
                });
                return true;
            });

        await CreateSut().UpdateEventAsync(
            id,
            new UpdateCalendarEventDto(
                "Planning moved", null, null, null, newTeamId,
                Instant.FromUtc(2026, 6, 1, 10, 0), Instant.FromUtc(2026, 6, 1, 11, 0),
                false, null, null),
            actor, TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.CalendarEventUpdated, AuditEntityTypes.CalendarEvent, id,
            Arg.Any<string>(), actor,
            newTeamId, AuditEntityTypes.Team);
    }

    [HumansFact]
    public async Task DeleteEventAsync_WritesOneAuditEntry_RelatedToTheOwningTeam()
    {
        var id = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.SoftDeleteAsync(id, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns((teamId, "Planning"));

        await CreateSut().DeleteEventAsync(id, actor, TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.CalendarEventDeleted, AuditEntityTypes.CalendarEvent, id,
            Arg.Any<string>(), actor,
            teamId, AuditEntityTypes.Team);
    }

    [HumansFact]
    public async Task DeleteEventAsync_WritesNothing_WhenTheEventWasAlreadyGone()
    {
        _repo.SoftDeleteAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(((Guid, string)?)null);

        await CreateSut().DeleteEventAsync(
            Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CancelOccurrenceAsync_WritesOneAuditEntry_WithNoRelatedTeam()
    {
        var eventId = Guid.NewGuid();
        var actor = Guid.NewGuid();

        await CreateSut().CancelOccurrenceAsync(
            eventId, Instant.FromUtc(2026, 6, 8, 10, 0), actor, TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.CalendarOccurrenceCancelled, AuditEntityTypes.CalendarEvent, eventId,
            Arg.Any<string>(), actor,
            null, null);
    }

    [HumansFact]
    public async Task OverrideOccurrenceAsync_WritesOneAuditEntry_WithNoRelatedTeam()
    {
        var eventId = Guid.NewGuid();
        var actor = Guid.NewGuid();

        await CreateSut().OverrideOccurrenceAsync(
            eventId, Instant.FromUtc(2026, 6, 8, 10, 0),
            new OverrideOccurrenceDto(
                Instant.FromUtc(2026, 6, 8, 14, 0), Instant.FromUtc(2026, 6, 8, 15, 0),
                null, null, null, null),
            actor, TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.CalendarOccurrenceOverridden, AuditEntityTypes.CalendarEvent, eventId,
            Arg.Any<string>(), actor,
            null, null);
    }
}
