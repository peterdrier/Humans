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

    private TicketHoldingsViewComponent BuildSut(CancellationToken ct = default) => new(_tickets, _earlyEntry)
    {
        ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = new DefaultHttpContext { RequestAborted = ct } },
        },
    };

    [HumansTheory]
    [Xunit.InlineData(ProfileCardViewMode.Self)]
    [Xunit.InlineData(ProfileCardViewMode.Admin)]
    public async Task AbandonedRequest_CancelsHoldingsRead(ProfileCardViewMode mode)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));
        _tickets.GetUserTicketHoldingsAsync(userId, aborted.Token)
            .Returns(Task.FromCanceled<UserTicketHoldings>(aborted.Token));
        var act = () => BuildSut(aborted.Token).InvokeAsync(userId, mode);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

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
