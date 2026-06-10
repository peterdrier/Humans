using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="MyTicketStubsViewComponent"/>: it renders the holder's owned
/// tickets (from the owner-filtered holdings read model) as stubs with the EE pill
/// and pendency stamped, and renders nothing when the holder owns none.
/// </summary>
public class MyTicketStubsViewComponentTests
{
    private readonly ITicketServiceRead _tickets = Substitute.For<ITicketServiceRead>();
    private readonly IEarlyEntryService _earlyEntry = Substitute.For<IEarlyEntryService>();

    private MyTicketStubsViewComponent BuildSut() => new(_tickets, _earlyEntry)
    {
        ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
        },
    };

    private static UserTicketHoldingRow Row(bool pending = false, Guid? transferId = null) => new(
        AttendeeId: Guid.NewGuid(),
        AttendeeName: "Ada Lovelace",
        AttendeeEmail: "ada@example.com",
        VendorTicketId: "TKT-001",
        TicketTypeName: "GA",
        Status: TicketAttendeeStatus.Valid,
        HasPendingOutgoingTransfer: pending,
        PendingTransferRequestId: transferId);

    [HumansFact]
    public async Task Renders_StubsWithEarlyEntryAndPendencyStamped()
    {
        var userId = Guid.NewGuid();
        var ee = new LocalDate(2026, 8, 24);
        var transferId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(1, [Row(pending: true, transferId: transferId)]));
        _earlyEntry.GetForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserEarlyEntry(ee, ["Camp: Flaming Lotus"]));

        var result = await BuildSut().InvokeAsync(userId);

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var stubs = view.ViewData!.Model.Should().BeAssignableTo<IReadOnlyList<TicketStubInfo>>().Subject;
        var stub = stubs.Should().ContainSingle().Subject;
        stub.EarlyEntryDate.Should().Be(ee);
        stub.HasPendingTransfer.Should().BeTrue();
        stub.PendingTransferRequestId.Should().Be(transferId);
    }

    [HumansFact]
    public async Task RendersNothing_WhenHolderOwnsNoTickets()
    {
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        var result = await BuildSut().InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
            .Which.Content.Should().BeEmpty();
        await _earlyEntry.DidNotReceive().GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
