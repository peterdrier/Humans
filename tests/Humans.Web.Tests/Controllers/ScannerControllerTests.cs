using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class ScannerControllerTests
{
    private static ScannerController NewController(ITicketServiceRead tickets)
    {
        var ctrl = new ScannerController(tickets);

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Card" },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return ctrl;
    }

    private static ITicketServiceRead TicketsWithAttendee(TicketAttendeeInfo attendee)
    {
        var order = new TicketOrderInfo(
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
            IsCurrentEvent: true,
            Attendees: new[] { attendee });

        var tickets = Substitute.For<ITicketServiceRead>();
        tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { order });
        return tickets;
    }

    [HumansFact]
    public async Task Card_MatchingBarcode_ReturnsFoundCard()
    {
        var attendee = new TicketAttendeeInfo(
            Id: Guid.NewGuid(),
            VendorTicketId: "vt-1",
            AttendeeName: "Ada Lovelace",
            AttendeeEmail: "ada@example.com",
            TicketTypeName: "General Admission",
            Price: 10m,
            Status: TicketAttendeeStatus.Valid,
            MatchedUserId: null,
            Barcode: "xyz34Qy5");
        var tickets = TicketsWithAttendee(attendee);
        var ctrl = NewController(tickets);

        var result = await ctrl.Card("xyz34Qy5", default);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Found.Should().BeTrue();
        vm.ScannedBarcode.Should().Be("xyz34Qy5");
        vm.TicketTypeName.Should().Be("General Admission");
        vm.Stub.Should().NotBeNull();
        vm.Stub!.AttendeeName.Should().Be("Ada Lovelace");
        vm.Stub.AttendeeEmail.Should().Be("ada@example.com");
        vm.Stub.Status.Should().Be(TicketAttendeeStatus.Valid);
    }

    [HumansFact]
    public async Task Card_VoidAttendeeWithTransfer_CarriesTransferFields()
    {
        var transferredAt = Instant.FromUtc(2026, 6, 5, 9, 0);
        var attendee = new TicketAttendeeInfo(
            Id: Guid.NewGuid(),
            VendorTicketId: "vt-void",
            AttendeeName: "Old Owner",
            AttendeeEmail: "old@example.com",
            TicketTypeName: "General Admission",
            Price: 10m,
            Status: TicketAttendeeStatus.Void,
            MatchedUserId: null,
            Barcode: "vb1",
            TransferredToName: "Alice Receiver",
            TransferredAt: transferredAt);
        var tickets = TicketsWithAttendee(attendee);
        var ctrl = NewController(tickets);

        var result = await ctrl.Card("vb1", default);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Found.Should().BeTrue();
        vm.TransferredToName.Should().Be("Alice Receiver");
        vm.TransferredAt.Should().Be(transferredAt);
    }

    [HumansFact]
    public async Task Card_UnmatchedBarcode_ReturnsNotFoundCard()
    {
        var attendee = new TicketAttendeeInfo(
            Id: Guid.NewGuid(),
            VendorTicketId: "vt-1",
            AttendeeName: "Ada Lovelace",
            AttendeeEmail: "ada@example.com",
            TicketTypeName: "General Admission",
            Price: 10m,
            Status: TicketAttendeeStatus.Valid,
            MatchedUserId: null,
            Barcode: "xyz34Qy5");
        var tickets = TicketsWithAttendee(attendee);
        var ctrl = NewController(tickets);

        var result = await ctrl.Card("nope-0000", default);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Found.Should().BeFalse();
        vm.ScannedBarcode.Should().Be("nope-0000");
        vm.Stub.Should().BeNull();
    }
}
