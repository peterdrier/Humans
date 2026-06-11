using AwesomeAssertions;
using Humans.Application;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using NodaTime;

namespace Humans.Web.Tests.Controllers;

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

    private static TicketOrderInfo Order(bool isCurrentEvent, params TicketAttendeeInfo[] attendees) =>
        new(
            Id: Guid.NewGuid(),
            VendorOrderId: "ord-1",
            BuyerName: "Buyer",
            BuyerEmail: "buyer@example.com",
            TotalAmount: 10m,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "evt-1",
            PurchasedAt: Instant.FromUtc(2026, 6, 1, 12, 0),
            MatchedUserId: null,
            IsCurrentEvent: isCurrentEvent,
            Attendees: attendees);

    [HumansFact]
    public void Find_CurrentEventExactBarcode_ReturnsAttendee()
    {
        var hit = Attendee("4b4DGpc");
        var orders = new[] { Order(isCurrentEvent: true, hit) };

        var result = TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "4b4DGpc");

        result.Should().BeSameAs(hit);
    }

    [HumansFact]
    public void Find_UnknownBarcode_ReturnsNull()
    {
        var orders = new[] { Order(isCurrentEvent: true, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "nope-000")
            .Should().BeNull();
    }

    [HumansFact]
    public void Find_PastEventBarcode_ReturnsNull()
    {
        var orders = new[] { Order(isCurrentEvent: false, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "4b4DGpc")
            .Should().BeNull();
    }

    [HumansFact]
    public void Find_DiffersByCase_ReturnsNull()
    {
        // Barcodes are case-sensitive (Ordinal) — gate-scanner contract.
        var orders = new[] { Order(isCurrentEvent: true, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "4b4dgpc")
            .Should().BeNull();
    }

    [HumansFact]
    public void Find_EmptyOrWhitespaceQuery_ReturnsNull()
    {
        var orders = new[] { Order(isCurrentEvent: true, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "   ").Should().BeNull();
        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "").Should().BeNull();
        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, null).Should().BeNull();
    }

    private static UserInfo ActiveHuman(Guid id, string burnerName) =>
        UserInfo.Create(
            new User { Id = id, PreferredLanguage = "en" },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                Id = Guid.NewGuid(),
                UserId = id,
                BurnerName = burnerName,
                State = ProfileState.Active,
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
        UserInfo.Create(
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
