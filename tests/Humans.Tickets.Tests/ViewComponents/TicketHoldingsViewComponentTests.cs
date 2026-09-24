using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Tickets.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="TicketHoldingsViewComponent"/>'s viewer gate (nobodies-collective/Humans#1815,
/// D-C): contributed to <c>UserPartSlots.ProfileSidebar</c> / <c>AdminDetailSidebar</c>, it has no
/// per-viewer visibility check of its own, so it must render nothing for a Public viewer and must
/// only show an empty-holdings card for Admin.
/// </summary>
public class TicketHoldingsViewComponentTests
{
    private readonly ITicketServiceRead _tickets = Substitute.For<ITicketServiceRead>();
    private readonly IEarlyEntryService _earlyEntry = Substitute.For<IEarlyEntryService>();

    private TicketHoldingsViewComponent BuildSut() => new(_tickets, _earlyEntry)
    {
        ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
        },
    };

    [HumansFact]
    public async Task RendersNothing_ForPublicViewer_EvenWithHoldings()
    {
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(1, []));

        var result = await BuildSut().InvokeAsync(userId, ProfileCardViewMode.Public);

        result.Should().BeOfType<ContentViewComponentResult>()
            .Which.Content.Should().BeEmpty();
        await _tickets.DidNotReceive().GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RendersNothing_ForSelfViewer_WithNoHoldings()
    {
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        var result = await BuildSut().InvokeAsync(userId, ProfileCardViewMode.Self);

        result.Should().BeOfType<ContentViewComponentResult>()
            .Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Renders_ForAdminViewer_EvenWithNoHoldings()
    {
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        var result = await BuildSut().InvokeAsync(userId, ProfileCardViewMode.Admin);

        result.Should().BeOfType<ViewViewComponentResult>();
    }
}
