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

    private readonly List<TicketAttendee> _unmatched = new();

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
