using AwesomeAssertions;
using Humans.Application;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
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
}
