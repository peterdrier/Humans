using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Testing;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="MyTicketStubsViewComponent"/>: it renders the holder's stubs
/// with the EE pill stamped, shows only tickets this account currently owns (not
/// ones it merely bought for another account), and renders nothing when there are none.
/// </summary>
public class MyTicketStubsViewComponentTests
{
    private readonly ITicketTransferService _transfer = Substitute.For<ITicketTransferService>();
    private readonly IEarlyEntryService _earlyEntry = Substitute.For<IEarlyEntryService>();

    private MyTicketStubsViewComponent BuildSut() => new(_transfer, _earlyEntry)
    {
        ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
        },
    };

    private static MyAttendeeRowDto Row(bool isCurrentOwner = true, string email = "ada@example.com") => new(
        AttendeeId: Guid.NewGuid(),
        AttendeeName: "Ada Lovelace",
        AttendeeEmail: email,
        VendorTicketId: "TKT-001",
        TicketTypeName: "GA",
        Status: TicketAttendeeStatus.Valid,
        IsCurrentOwner: isCurrentOwner,
        CanSendTransfer: true,
        HasPendingOutgoingTransfer: false,
        PendingTransferRequestId: null);

    [HumansFact]
    public async Task Renders_StubsWithEarlyEntryStamped()
    {
        var userId = Guid.NewGuid();
        var ee = new LocalDate(2026, 8, 24);
        _transfer.GetMyAttendeesAsync(userId, Arg.Any<CancellationToken>()).Returns([Row()]);
        _earlyEntry.GetForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserEarlyEntry(ee, ["Camp: Flaming Lotus"]));

        var result = await BuildSut().InvokeAsync(userId);

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var stubs = view.ViewData!.Model.Should().BeAssignableTo<IReadOnlyList<TicketStubInfo>>().Subject;
        stubs.Should().ContainSingle().Which.EarlyEntryDate.Should().Be(ee);
    }

    [HumansFact]
    public async Task RendersNothing_WhenHolderHasNoTickets()
    {
        var userId = Guid.NewGuid();
        _transfer.GetMyAttendeesAsync(userId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await BuildSut().InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
            .Which.Content.Should().BeEmpty();
        await _earlyEntry.DidNotReceive().GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExcludesTicketsOwnedByAnotherAccount()
    {
        // A buyer also sees attendees on orders they purchased; a ticket whose attendee
        // has their own account (IsCurrentOwner: false) belongs on that account's strip.
        var userId = Guid.NewGuid();
        _transfer.GetMyAttendeesAsync(userId, Arg.Any<CancellationToken>())
            .Returns([Row(), Row(isCurrentOwner: false, email: "daughter@example.com")]);
        _earlyEntry.GetForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserEarlyEntry(new LocalDate(2026, 8, 24), []));

        var result = await BuildSut().InvokeAsync(userId);

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var stubs = view.ViewData!.Model.Should().BeAssignableTo<IReadOnlyList<TicketStubInfo>>().Subject;
        stubs.Should().ContainSingle().Which.AttendeeEmail.Should().Be("ada@example.com");
    }

    [HumansFact]
    public async Task RendersNothing_WhenOnlyTicketsAreOwnedByOthers()
    {
        var userId = Guid.NewGuid();
        _transfer.GetMyAttendeesAsync(userId, Arg.Any<CancellationToken>())
            .Returns([Row(isCurrentOwner: false)]);

        var result = await BuildSut().InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
            .Which.Content.Should().BeEmpty();
        await _earlyEntry.DidNotReceive().GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
