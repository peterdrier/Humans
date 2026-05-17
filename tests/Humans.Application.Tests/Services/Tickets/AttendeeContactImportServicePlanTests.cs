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

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeContactImportServicePlanTests
{
    [HumansFact]
    public async Task Plan_AttendeeWithoutEmail_ClassifiedAsSkipNoEmail()
    {
        var harness = new PlanHarness();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_1",
            AttendeeEmail = null,
            AttendeeName = "Jane Doe",
            Status = TicketAttendeeStatus.Valid,
        });

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Should().ContainSingle()
            .Which.Outcome.Should().Be(AttendeeImportOutcome.SkipNoEmail);
    }

    [HumansFact]
    public async Task Plan_SingleVerifiedMatch_ClassifiedAsAttachVerified()
    {
        var harness = new PlanHarness();
        var userId = Guid.NewGuid();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_v",
            AttendeeEmail = "jane@x.com",
            AttendeeName = "Jane Doe",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.UserEmails.GetDistinctVerifiedUserIdsAsync("jane@x.com", Arg.Any<CancellationToken>())
            .Returns([userId]);
        harness.Users.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, MergedToUserId = null });

        var plan = await harness.Service.BuildPlanAsync();

        var decision = plan.Decisions.Single();
        decision.Outcome.Should().Be(AttendeeImportOutcome.AttachVerified);
        decision.TargetUserId.Should().Be(userId);
        decision.AttendeeName.Should().Be("Jane Doe");
    }

    [HumansFact]
    public async Task Plan_AttachVerified_FollowsMergedTombstone()
    {
        var harness = new PlanHarness();
        var deadId = Guid.NewGuid();
        var liveId = Guid.NewGuid();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_v",
            AttendeeEmail = "jane@x.com",
            AttendeeName = "Jane",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.UserEmails.GetDistinctVerifiedUserIdsAsync("jane@x.com", Arg.Any<CancellationToken>())
            .Returns([deadId]);
        harness.Users.GetByIdAsync(deadId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = deadId, MergedToUserId = liveId });
        harness.Users.GetByIdAsync(liveId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = liveId, MergedToUserId = null });

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().TargetUserId.Should().Be(liveId);
    }

    [HumansFact]
    public async Task Plan_UnverifiedMatchOnly_ClassifiedAsDeleteUnverifiedThenCreate()
    {
        var harness = new PlanHarness();
        var squatterUserId = Guid.NewGuid();
        var unverifiedRowId = Guid.NewGuid();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_v",
            AttendeeEmail = "victim@x.com",
            AttendeeName = "Victim",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.UserEmails.GetDistinctVerifiedUserIdsAsync("victim@x.com", Arg.Any<CancellationToken>())
            .Returns([]);
        harness.UserEmails.FindAnyEmailRowByAddressAsync("victim@x.com", Arg.Any<CancellationToken>())
            .Returns((squatterUserId, unverifiedRowId));

        var plan = await harness.Service.BuildPlanAsync();

        var decision = plan.Decisions.Single();
        decision.Outcome.Should().Be(AttendeeImportOutcome.DeleteUnverifiedThenCreate);
        decision.UnverifiedRowUserId.Should().Be(squatterUserId);
        decision.UnverifiedEmailIdToDelete.Should().Be(unverifiedRowId);
    }

    [HumansFact]
    public async Task Plan_NoUserEmailMatch_ClassifiedAsCreateNewUser()
    {
        var harness = new PlanHarness();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_v",
            AttendeeEmail = "fresh@x.com",
            AttendeeName = "Fresh Face",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.UserEmails.GetDistinctVerifiedUserIdsAsync("fresh@x.com", Arg.Any<CancellationToken>())
            .Returns([]);
        harness.UserEmails.FindAnyEmailRowByAddressAsync("fresh@x.com", Arg.Any<CancellationToken>())
            .Returns(((Guid, Guid)?)null);

        var plan = await harness.Service.BuildPlanAsync();

        var decision = plan.Decisions.Single();
        decision.Outcome.Should().Be(AttendeeImportOutcome.CreateNewUser);
        decision.AttendeeName.Should().Be("Fresh Face");
    }

    [HumansFact]
    public async Task Plan_MultipleAttendeesSameEmail_CollapseToOneDecisionWithGroup()
    {
        var harness = new PlanHarness();
        var leadId = Guid.NewGuid();
        var extra1 = Guid.NewGuid();
        var extra2 = Guid.NewGuid();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = leadId,
            VendorTicketId = "tkt_1",
            AttendeeEmail = "buyer@x.com",
            AttendeeName = "Sara Smith",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.AddUnmatched(new TicketAttendee
        {
            Id = extra1,
            VendorTicketId = "tkt_2",
            AttendeeEmail = "buyer@x.com",
            AttendeeName = "Sara S.",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.AddUnmatched(new TicketAttendee
        {
            Id = extra2,
            VendorTicketId = "tkt_3",
            // Case-insensitive grouping: trim + casefold should still group.
            AttendeeEmail = "BUYER@x.com",
            AttendeeName = "Sara Smith",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.UserEmails.GetDistinctVerifiedUserIdsAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        harness.UserEmails.FindAnyEmailRowByAddressAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(((Guid, Guid)?)null);

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Should().ContainSingle();
        var d = plan.Decisions[0];
        d.Outcome.Should().Be(AttendeeImportOutcome.CreateNewUser);
        d.AttendeeId.Should().Be(leadId);
        d.AdditionalAttendeeIds.Should().BeEquivalentTo([extra1, extra2]);
        d.ObservedNames.Should().BeEquivalentTo(new[] { "Sara Smith", "Sara S." });
        plan.TotalUnmatched.Should().Be(3);
    }

    [HumansFact]
    public async Task Plan_NoEmailAttendees_RemainPerAttendee_NotGrouped()
    {
        var harness = new PlanHarness();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_a",
            AttendeeEmail = null,
            AttendeeName = "A",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_b",
            AttendeeEmail = "   ",
            AttendeeName = "B",
            Status = TicketAttendeeStatus.Valid,
        });

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Should().HaveCount(2);
        plan.Decisions.Should().OnlyContain(d => d.Outcome == AttendeeImportOutcome.SkipNoEmail);
        plan.Decisions.Should().OnlyContain(d => d.AdditionalAttendeeIds == null);
    }

    [HumansFact]
    public async Task Plan_MultipleVerifiedMatches_ClassifiedAsAmbiguous()
    {
        var harness = new PlanHarness();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_v",
            AttendeeEmail = "shared@x.com",
            Status = TicketAttendeeStatus.Valid,
        });
        harness.UserEmails.GetDistinctVerifiedUserIdsAsync("shared@x.com", Arg.Any<CancellationToken>())
            .Returns([u1, u2]);

        var plan = await harness.Service.BuildPlanAsync();

        var decision = plan.Decisions.Single();
        decision.Outcome.Should().Be(AttendeeImportOutcome.AmbiguousMultipleVerified);
        decision.AmbiguousUserIds.Should().BeEquivalentTo([u1, u2]);
    }
}

internal sealed class PlanHarness
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

    public PlanHarness()
    {
        TicketRepo.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new TicketSyncState { Id = 1, VendorEventId = "evt_active" });
        TicketRepo.GetUnmatchedActiveAttendeesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => _unmatched);
    }

    public void AddUnmatched(TicketAttendee a) => _unmatched.Add(a);

    public AttendeeContactImportService Service => new(
        TicketRepo, UserEmails, Provisioning, Users, Shifts, TicketQuery, Audit, Clock,
        NullLogger<AttendeeContactImportService>.Instance);
}
