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
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

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
            Id = attendeeId,
            VendorTicketId = "tkt_v",
            VendorEventId = "evt_active",
            AttendeeEmail = "jane@x.com",
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = null,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);

        var plan = new AttendeeImportPlan([
                new AttendeeImportDecision(
                    attendeeId, "jane@x.com", "Jane Doe", "tkt_v",
                    AttendeeImportOutcome.AttachVerified,
                    TargetUserId: targetUserId,
                    UnverifiedEmailIdToDelete: null,
                    UnverifiedRowUserId: null,
                    AmbiguousUserIds: null)
            ],
            TotalUnmatched: 1);

        var actorId = Guid.NewGuid();
        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId }, actorId);

        result.AttachedToExistingVerified.Should().Be(1);
        result.UsersCreated.Should().Be(0);
        attendee.MatchedUserId.Should().Be(targetUserId);

        await harness.TicketRepo.Received(1)
            .UpsertAttendeesAsync(Arg.Is<IReadOnlyList<TicketAttendee>>(
                l => l.Count == 1 && l[0].Id == attendeeId), Arg.Any<CancellationToken>());

        await harness.Users.Received(1).SetParticipationFromTicketSyncAsync(
            targetUserId, 2026, ParticipationStatus.Ticketed,
            (Instant?)null, Arg.Any<CancellationToken>());

        harness.TicketQuery.Received(1).InvalidateAfterContactImport();

        await harness.Audit.Received(1).LogAsync(
            AuditAction.TicketContactsImported,
            "Tickets", Guid.Empty,
            Arg.Is<string>(s => s.Contains("attached=1")),
            actorId);
    }

    [HumansFact]
    public async Task Apply_CreateNewUser_CallsProvisioningWithAttendeeName_AndTicketTailorSource()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId,
            VendorTicketId = "tkt_v",
            VendorEventId = "evt_active",
            AttendeeEmail = "fresh@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "fresh@x.com", "Fresh Face", ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(
                new User { Id = newUserId }, Created: true));

        var plan = new AttendeeImportPlan([
                new AttendeeImportDecision(
                    attendeeId, "fresh@x.com", "Fresh Face", "tkt_v",
                    AttendeeImportOutcome.CreateNewUser,
                    null, null, null, null)
            ],
            1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId }, Guid.NewGuid());

        result.UsersCreated.Should().Be(1);
        attendee.MatchedUserId.Should().Be(newUserId);

        await harness.Provisioning.Received(1).FindOrCreateUserByEmailAsync(
            "fresh@x.com", "Fresh Face", ContactSource.TicketTailor, Arg.Any<CancellationToken>());

        await harness.Users.Received(1).SetParticipationFromTicketSyncAsync(
            newUserId, 2026, ParticipationStatus.Ticketed,
            (Instant?)null, Arg.Any<CancellationToken>());
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
            Id = attendeeId,
            VendorTicketId = "tkt_v",
            VendorEventId = "evt_active",
            AttendeeEmail = "victim@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", "Victim", ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = newUserId }, true));

        var plan = new AttendeeImportPlan([
                new AttendeeImportDecision(
                    attendeeId, "victim@x.com", "Victim", "tkt_v",
                    AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                    TargetUserId: null,
                    UnverifiedEmailIdToDelete: squatterEmailId,
                    UnverifiedRowUserId: squatterId,
                    AmbiguousUserIds: null)
            ],
            1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId }, Guid.NewGuid());

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

    [HumansFact]
    public async Task Apply_OnlyProcessesSelectedAttendees()
    {
        var harness = new ApplyHarness();
        var pickedId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();
        var picked = new TicketAttendee
        {
            Id = pickedId,
            VendorTicketId = "tkt_p",
            VendorEventId = "evt_active",
            AttendeeEmail = "p@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        var unselected = new TicketAttendee
        {
            Id = skippedId,
            VendorTicketId = "tkt_s",
            VendorEventId = "evt_active",
            AttendeeEmail = "s@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(picked);
        harness.WithUnmatched(unselected);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                Arg.Any<string>(), Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(_ => new AccountProvisioningResult(new User { Id = Guid.NewGuid() }, true));

        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(pickedId, "p@x.com", "P", "tkt_p",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null),
                new AttendeeImportDecision(skippedId, "s@x.com", "S", "tkt_s",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null)
        ], 2);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { pickedId }, Guid.NewGuid());

        result.TotalAttempted.Should().Be(1);
        result.UsersCreated.Should().Be(1);
        picked.MatchedUserId.Should().NotBeNull();
        unselected.MatchedUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task Apply_GroupedDecision_FansMatchedUserIdToAllGroupAttendees()
    {
        var harness = new ApplyHarness();
        var leadId = Guid.NewGuid();
        var extraId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var lead = new TicketAttendee
        {
            Id = leadId,
            VendorTicketId = "tkt_1",
            VendorEventId = "evt_active",
            AttendeeEmail = "buyer@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        var extra = new TicketAttendee
        {
            Id = extraId,
            VendorTicketId = "tkt_2",
            VendorEventId = "evt_active",
            AttendeeEmail = "buyer@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(lead);
        harness.WithUnmatched(extra);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "buyer@x.com", "Sara Smith", ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = newUserId }, true));

        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(
                leadId, "buyer@x.com", "Sara Smith", "tkt_1",
                AttendeeImportOutcome.CreateNewUser,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null,
                AdditionalAttendeeIds: [extraId],
                ObservedNames: ["Sara Smith"])
        ], 2);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { leadId }, Guid.NewGuid());

        result.TotalAttempted.Should().Be(1);
        result.UsersCreated.Should().Be(1);
        lead.MatchedUserId.Should().Be(newUserId);
        extra.MatchedUserId.Should().Be(newUserId);

        // Provisioning runs once even though two attendees share the email.
        await harness.Provisioning.Received(1).FindOrCreateUserByEmailAsync(
            "buyer@x.com", "Sara Smith", ContactSource.TicketTailor, Arg.Any<CancellationToken>());

        // Upsert receives both rows so DB is updated for every grouped attendee.
        await harness.TicketRepo.Received(1)
            .UpsertAttendeesAsync(Arg.Is<IReadOnlyList<TicketAttendee>>(
                l => l.Count == 2
                    && l.Any(x => x.Id == leadId)
                    && l.Any(x => x.Id == extraId)),
                Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_GroupedDecision_AttachVerified_FansToAllGroupAttendees()
    {
        var harness = new ApplyHarness();
        var leadId = Guid.NewGuid();
        var extraId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var lead = new TicketAttendee
        {
            Id = leadId,
            VendorTicketId = "tkt_1",
            VendorEventId = "evt_active",
            AttendeeEmail = "buyer@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        var extra = new TicketAttendee
        {
            Id = extraId,
            VendorTicketId = "tkt_2",
            VendorEventId = "evt_active",
            AttendeeEmail = "buyer@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(lead);
        harness.WithUnmatched(extra);
        harness.WithActiveYear(2026);

        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(
                leadId, "buyer@x.com", "Sara Smith", "tkt_1",
                AttendeeImportOutcome.AttachVerified,
                TargetUserId: targetUserId,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null,
                AdditionalAttendeeIds: [extraId])
        ], 2);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { leadId }, Guid.NewGuid());

        result.AttachedToExistingVerified.Should().Be(1);
        lead.MatchedUserId.Should().Be(targetUserId);
        extra.MatchedUserId.Should().Be(targetUserId);

        // Participation flips once for the single user, not twice.
        await harness.Users.Received(1).SetParticipationFromTicketSyncAsync(
            targetUserId, 2026, ParticipationStatus.Ticketed,
            (Instant?)null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_GroupedDecision_GroupMemberWithDriftedEmail_IsSkippedButLeadProceeds()
    {
        var harness = new ApplyHarness();
        var leadId = Guid.NewGuid();
        var driftedId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var lead = new TicketAttendee
        {
            Id = leadId,
            VendorTicketId = "tkt_1",
            VendorEventId = "evt_active",
            AttendeeEmail = "buyer@x.com",
            Status = TicketAttendeeStatus.Valid,
        };
        var drifted = new TicketAttendee
        {
            Id = driftedId,
            VendorTicketId = "tkt_2",
            VendorEventId = "evt_active",
            AttendeeEmail = "different@x.com", // email changed since plan
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(lead);
        harness.WithUnmatched(drifted);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "buyer@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = newUserId }, true));

        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(
                leadId, "buyer@x.com", "Sara Smith", "tkt_1",
                AttendeeImportOutcome.CreateNewUser,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null,
                AdditionalAttendeeIds: [driftedId])
        ], 2);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { leadId }, Guid.NewGuid());

        // Lead still wins; drifted is silently skipped (logged), not vanished.
        result.UsersCreated.Should().Be(1);
        lead.MatchedUserId.Should().Be(newUserId);
        drifted.MatchedUserId.Should().BeNull();
        result.VanishedBetweenPlanAndApply.Should().Be(0);
    }

    [HumansFact]
    public async Task Apply_AttendeeVanishedBetweenPlanAndApply_IsCountedAndSkipped()
    {
        var harness = new ApplyHarness();
        var goneId = Guid.NewGuid();
        // No call to harness.WithUnmatched — the re-query returns empty.
        harness.WithActiveYear(2026);

        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(goneId, "g@x.com", "G", "tkt_g",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null)
        ], 1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { goneId }, Guid.NewGuid());

        result.VanishedBetweenPlanAndApply.Should().Be(1);
        result.UsersCreated.Should().Be(0);

        await harness.Provisioning.DidNotReceive().FindOrCreateUserByEmailAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<ContactSource>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_PerAttendeeFailure_DoesNotAbortBatch()
    {
        var harness = new ApplyHarness();
        var failId = Guid.NewGuid();
        var okId = Guid.NewGuid();
        harness.WithUnmatched(new TicketAttendee
        {
            Id = failId,
            VendorTicketId = "tkt_f",
            VendorEventId = "evt_active",
            AttendeeEmail = "f@x.com",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.WithUnmatched(new TicketAttendee
        {
            Id = okId,
            VendorTicketId = "tkt_o",
            VendorEventId = "evt_active",
            AttendeeEmail = "o@x.com",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "f@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "o@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = Guid.NewGuid() }, true));

        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(failId, "f@x.com", "F", "tkt_f",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null),
                new AttendeeImportDecision(okId, "o@x.com", "O", "tkt_o",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null)
        ], 2);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { failId, okId }, Guid.NewGuid());

        result.Errors.Should().Be(1);
        result.UsersCreated.Should().Be(1);
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

    private readonly List<TicketAttendee> _unmatched = [];

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
