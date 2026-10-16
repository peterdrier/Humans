using NSubstitute.ExceptionExtensions;
using AwesomeAssertions;
using Humans.Tickets.Services.Dtos;
using NSubstitute;
using Humans.Tickets.Domain;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.Tickets.Tests.Services;

public class AttendeeContactImportServiceSquatterTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Squatter_ReplacementAttachesOnlyAfterOwnerOperationSucceeds(bool fails)
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
        var replacement = harness.Provisioning.ReplaceUnverifiedEmailAndProvisionAsync(
            squatterUserId, squatterRowId, "victim@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>());
        if (fails) replacement.ThrowsAsync(new InvalidOperationException("Provisioning failed"));
        else replacement.Returns(new AccountProvisioningResult(new User { Id = newVictimUserId }, Created: true));

        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(
                    attendeeId, "victim@x.com", "Victim", "tkt_v",
                    AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                    null, squatterRowId, squatterUserId, null)
        ], 1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId }, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        attendee.MatchedUserId.Should().Be(fails ? null : newVictimUserId);
        result.Errors.Should().Be(fails ? 1 : 0);
        result.UsersCreated.Should().Be(fails ? 0 : 1);
        result.UnverifiedRowsDeletedAndUserCreated.Should().Be(fails ? 0 : 1);
        attendee.MatchedUserId.Should().NotBe(squatterUserId);
        await harness.Provisioning.Received(1).ReplaceUnverifiedEmailAndProvisionAsync(
            squatterUserId, squatterRowId, "victim@x.com", "Victim", ContactSource.TicketTailor, Arg.Any<CancellationToken>());
        await harness.UserEmails.DidNotReceiveWithAnyArgs().DeleteEmailAsync(default, default, default);
    }
}
