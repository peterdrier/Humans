using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeContactImportServiceSquatterTests
{
    [HumansFact]
    public async Task Squatter_UnverifiedRowDeletedBeforeNewUserCreated_NewUserNotAttachedToSquatter()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var squatterUserId = Guid.NewGuid();
        var squatterRowId = Guid.NewGuid();
        var newVictimUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId,
            VendorTicketId = "tkt_v",
            VendorEventId = "evt_active",
            AttendeeEmail = "victim@x.com",
            AttendeeName = "Victim",
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = newVictimUserId }, Created: true));

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "victim@x.com", "Victim", "tkt_v",
                    AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                    null, squatterRowId, squatterUserId, null),
            }, 1);

        await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId }, Guid.NewGuid());

        // 1. Squatter row deleted.
        await harness.UserEmails.Received(1)
            .DeleteEmailAsync(squatterUserId, squatterRowId, Arg.Any<CancellationToken>());

        // 2. New user created — NOT attached to squatter.
        attendee.MatchedUserId.Should().Be(newVictimUserId);
        attendee.MatchedUserId.Should().NotBe(squatterUserId);

        // 3. Delete happened before create.
        Received.InOrder(() =>
        {
            _ = harness.UserEmails.DeleteEmailAsync(squatterUserId, squatterRowId, Arg.Any<CancellationToken>());
            _ = harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>());
        });
    }
}
