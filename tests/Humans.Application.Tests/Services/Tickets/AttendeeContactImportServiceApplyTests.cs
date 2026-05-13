using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeContactImportServiceApplyTests
{
    [HumansFact]
    public async Task Apply_AttachVerified_SetsMatchedUserIdAndUpserts()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId, VendorTicketId = "tkt_v", VendorEventId = "evt_active",
            AttendeeEmail = "jane@x.com", Status = TicketAttendeeStatus.Valid,
            MatchedUserId = null,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "jane@x.com", "Jane Doe", "tkt_v",
                    AttendeeImportOutcome.AttachVerified,
                    TargetUserId: targetUserId,
                    UnverifiedEmailIdToDelete: null,
                    UnverifiedRowUserId: null,
                    AmbiguousUserIds: null),
            },
            TotalUnmatched: 1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId });

        result.AttachedToExistingVerified.Should().Be(1);
        result.UsersCreated.Should().Be(0);
        attendee.MatchedUserId.Should().Be(targetUserId);

        await harness.TicketRepo.Received(1)
            .UpsertAttendeesAsync(Arg.Is<IReadOnlyList<TicketAttendee>>(
                l => l.Count == 1 && l[0].Id == attendeeId), Arg.Any<CancellationToken>());

        await harness.Users.Received(1).SetParticipationFromTicketSyncAsync(
            targetUserId, 2026, ParticipationStatus.Ticketed, Arg.Any<CancellationToken>());

        harness.TicketQuery.Received(1).InvalidateAfterContactImport();

        await harness.Audit.Received(1).LogAsync(
            AuditAction.TicketContactsImported,
            "Tickets", Guid.Empty,
            Arg.Is<string>(s => s.Contains("attached=1")),
            nameof(AttendeeContactImportService));
    }

    [HumansFact]
    public async Task Apply_CreateNewUser_CallsProvisioningWithAttendeeName_AndTicketTailorSource()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId, VendorTicketId = "tkt_v", VendorEventId = "evt_active",
            AttendeeEmail = "fresh@x.com", Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "fresh@x.com", "Fresh Face", ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(
                new User { Id = newUserId }, Created: true));

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "fresh@x.com", "Fresh Face", "tkt_v",
                    AttendeeImportOutcome.CreateNewUser,
                    null, null, null, null),
            },
            1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId });

        result.UsersCreated.Should().Be(1);
        attendee.MatchedUserId.Should().Be(newUserId);

        await harness.Provisioning.Received(1).FindOrCreateUserByEmailAsync(
            "fresh@x.com", "Fresh Face", ContactSource.TicketTailor, Arg.Any<CancellationToken>());

        await harness.Users.Received(1).SetParticipationFromTicketSyncAsync(
            newUserId, 2026, ParticipationStatus.Ticketed, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_DeleteUnverifiedThenCreate_DeletesSquatterRowFirst()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var squatterId = Guid.NewGuid();
        var squatterEmailId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId, VendorTicketId = "tkt_v", VendorEventId = "evt_active",
            AttendeeEmail = "victim@x.com", Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", "Victim", ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = newUserId }, true));

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "victim@x.com", "Victim", "tkt_v",
                    AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                    TargetUserId: null,
                    UnverifiedEmailIdToDelete: squatterEmailId,
                    UnverifiedRowUserId: squatterId,
                    AmbiguousUserIds: null),
            },
            1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId });

        result.UnverifiedRowsDeletedAndUserCreated.Should().Be(1);
        result.UsersCreated.Should().Be(1);
        attendee.MatchedUserId.Should().Be(newUserId);

        Received.InOrder(() =>
        {
            _ = harness.UserEmails.DeleteEmailAsync(squatterId, squatterEmailId, Arg.Any<CancellationToken>());
            _ = harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", "Victim", ContactSource.TicketTailor, Arg.Any<CancellationToken>());
        });
    }
}

internal sealed class ApplyHarness
{
    public ITicketRepository TicketRepo { get; } = Substitute.For<ITicketRepository>();
    public IUserEmailService UserEmails { get; } = Substitute.For<IUserEmailService>();
    public IAccountProvisioningService Provisioning { get; } = Substitute.For<IAccountProvisioningService>();
    public IUserService Users { get; } = Substitute.For<IUserService>();
    public IShiftManagementService Shifts { get; } = Substitute.For<IShiftManagementService>();
    public ITicketQueryService TicketQuery { get; } = Substitute.For<ITicketQueryService>();
    public IAuditLogService Audit { get; } = Substitute.For<IAuditLogService>();
    public FakeClock Clock { get; } = new(Instant.FromUtc(2026, 5, 13, 12, 0));

    private readonly List<TicketAttendee> _unmatched = new();

    public ApplyHarness()
    {
        TicketRepo.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new TicketSyncState { Id = 1, VendorEventId = "evt_active" });
        TicketRepo.GetUnmatchedActiveAttendeesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => _unmatched);
    }

    public void WithUnmatched(TicketAttendee a) => _unmatched.Add(a);

    public void WithActiveYear(int year)
    {
        Shifts.GetActiveAsync().Returns(new EventSettings { Year = year, IsActive = true });
    }

    public AttendeeContactImportService Service => new(
        TicketRepo, UserEmails, Provisioning, Users, Shifts, TicketQuery, Audit, Clock,
        NullLogger<AttendeeContactImportService>.Instance);
}
