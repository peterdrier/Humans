using AwesomeAssertions;
using Humans.Teams.Controllers;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;
using NodaTime;

namespace Humans.Teams.Tests.Controllers;

public class TeamAdminControllerTicketLookupTests
{
    private static TicketAttendeeInfo Attendee(string barcode, Guid? matchedUserId = null) =>
        new(
            Id: Guid.NewGuid(),
            VendorTicketId: "vt-" + barcode,
            AttendeeName: "Ada Lovelace",
            AttendeeEmail: "ada@example.com",
            TicketTypeName: "General Admission",
            Price: 10m,
            Status: TicketAttendeeStatus.Valid,
            MatchedUserId: matchedUserId,
            Barcode: barcode);

    private static UserInfo ActiveHuman(Guid id, string burnerName) =>
        UserInfoFactory.Create(
            // BurnerName mirrors CopyNamesToUser's dual-write from Profile onto User (#1097) —
            // UserInfo.BurnerName reads User.BurnerName only (#1098).
            new User { Id = id, PreferredLanguage = "en", BurnerName = burnerName },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                Id = Guid.NewGuid(),
                UserId = id,
                BurnerName = burnerName,
                IsApproved = true,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    // No profile => UserInfo.IsActive == false (deleted/rejected/stub).
    private static UserInfo InactiveHuman(Guid id) =>
        UserInfoFactory.Create(
            new User { Id = id, PreferredLanguage = "en" },
            [], [], [], profile: null, [], [], [], []);

    [HumansFact]
    public void BuildRows_ActiveMatchedHuman_ReturnsOneRow()
    {
        var userId = Guid.NewGuid();
        var hit = Attendee("4b4DGpc", matchedUserId: userId);

        var rows = TeamAdminController.BuildTicketLookupRows(
            hit, ActiveHuman(userId, "Ada"), detailLabel: "Ticket #4b4DGpc");

        rows.Should().ContainSingle();
        rows[0].UserId.Should().Be(userId);
        rows[0].DisplayName.Should().Be("Ada");
        rows[0].Detail.Should().Be("Ticket #4b4DGpc");
    }

    [HumansFact]
    public void BuildRows_NullHit_ReturnsEmpty() =>
        TeamAdminController.BuildTicketLookupRows(null, null, "x").Should().BeEmpty();

    [HumansFact]
    public void BuildRows_AttendeeNotPairedToHuman_ReturnsEmpty()
    {
        var hit = Attendee("4b4DGpc", matchedUserId: null);

        TeamAdminController.BuildTicketLookupRows(hit, null, "Ticket #4b4DGpc")
            .Should().BeEmpty();
    }

    [HumansFact]
    public void BuildRows_MatchedHumanInactive_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        var hit = Attendee("4b4DGpc", matchedUserId: userId);

        TeamAdminController.BuildTicketLookupRows(hit, InactiveHuman(userId), "Ticket #4b4DGpc")
            .Should().BeEmpty();
    }
}
