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
    [HumansFact]
    public async Task Squatter_DomainAliases_ReplaceOnceAndAttachEveryAttendee()
    {
        var harness = new PlanHarness();
        var ownerId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var attendees = new[] { "victim@gmail.com", "victim@googlemail.com" }
            .Select((email, i) => new TicketAttendee
            {
                Id = Guid.NewGuid(),
                VendorTicketId = $"tkt_{i}",
                VendorEventId = "evt_active",
                AttendeeEmail = email,
                AttendeeName = "Victim",
                Status = TicketAttendeeStatus.Valid,
            }).ToList();
        foreach (var attendee in attendees) harness.AddUnmatched(attendee);
        harness.UserEmails.FindByAddressAsync(Arg.Any<string>(), true, true, Arg.Any<CancellationToken>()).Returns([]);
        harness.UserEmails.FindByAddressAsync(Arg.Any<string>(), true, false, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(ownerId, "victim@gmail.com", verified: false, id: emailId)]);
        var replacements = 0;
        harness.Provisioning.ReplaceUnverifiedEmailAndProvisionAsync(ownerId, emailId, Arg.Any<string>(),
                Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(_ => ++replacements == 1
                ? new AccountProvisioningResult(newUserId, Created: true)
                : throw new InvalidOperationException("The unverified replacement plan is stale."));
        var service = harness.Service;
        var plan = await service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(plan, attendees.Select(a => a.Id).ToHashSet(), Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Errors.Should().Be(0);
        result.TotalAttempted.Should().Be(1);
        result.UnverifiedRowsDeletedAndUserCreated.Should().Be(1);
        replacements.Should().Be(1);
        attendees.Should().OnlyContain(a => a.MatchedUserId == newUserId);
        await harness.TicketRepo.Received(1).UpsertAttendeesAsync(
            Arg.Is<IReadOnlyList<TicketAttendee>>(rows => rows.Count == 2), Arg.Any<CancellationToken>());
    }

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
        else replacement.Returns(new AccountProvisioningResult(newVictimUserId, Created: true));

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
        await harness.UserEmails.DidNotReceiveWithAnyArgs().DeleteEmailAsync(default, default);
    }
}
