using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Tickets.Tests.ViewComponents;

public class MyTicketStubsViewComponentTests
{
    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
    private readonly IEarlyEntryService _earlyEntry = Substitute.For<IEarlyEntryService>();

    private MyTicketStubsViewComponent BuildSut() => new(_tickets, _earlyEntry)
    {
        ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
        },
    };

    private static UserTicketHoldingRow Row() => new(
        AttendeeId: Guid.NewGuid(),
        AttendeeName: "Ada Lovelace",
        AttendeeEmail: "ada@example.com",
        Status: TicketAttendeeStatus.Valid);

    [HumansFact]
    public async Task Renders_StubsWithEarlyEntryStamped()
    {
        var userId = Guid.NewGuid();
        var ee = new LocalDate(2026, 8, 24);
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(1, [Row()]));
        _earlyEntry.GetForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new EarlyEntryRosterRow(userId, ee, ["Camp: Flaming Lotus"], false));

        var result = await BuildSut().InvokeAsync(userId);

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var stubs = view.ViewData!.Model.Should().BeAssignableTo<IReadOnlyList<TicketStubInfo>>().Subject;
        var stub = stubs.Should().ContainSingle().Subject;
        stub.EarlyEntryDate.Should().Be(ee);
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
