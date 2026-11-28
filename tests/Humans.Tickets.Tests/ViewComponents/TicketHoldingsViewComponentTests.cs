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
/// The component has no per-viewer visibility check of its own, so it must render nothing for a
/// Public viewer and only show an empty-holdings card for Admin.
/// </summary>
public class TicketHoldingsViewComponentTests
{
    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
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
    public async Task Holdings_SelfViewerNoHoldings_RendersOnsiteChip()
    {
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        var result = await BuildSut().InvokeAsync(userId, ProfileCardViewMode.Self);

        result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<TicketHoldingsViewModel>().Which.ChipOnly.Should().BeTrue();
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
