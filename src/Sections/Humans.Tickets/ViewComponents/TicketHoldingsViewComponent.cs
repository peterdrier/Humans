using Humans.Base.Enums;
using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// Renders nothing for <see cref="ProfileCardViewMode.Public"/>: it has no viewer check of its own and would leak any
/// member's holdings.
/// </summary>
public sealed class TicketHoldingsViewComponent(
    ITicketService queryService,
    IEarlyEntryService earlyEntryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode == ProfileCardViewMode.Public)
            return Content(string.Empty);

        var showEmpty = viewMode == ProfileCardViewMode.Admin;
        var holdings = await queryService.GetUserTicketHoldingsAsync(userId, HttpContext.RequestAborted);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.Tickets.Count == 0)
        {
            // No card, but a member checked in on a ticket they no longer hold still sees their own onsite chip.
            return viewMode == ProfileCardViewMode.Self
                ? View(new TicketHoldingsViewModel(userId, viewMode, 0, [], ChipOnly: true))
                : Content(string.Empty);
        }

        var earlyEntry = await earlyEntryService.GetForUserAsync(userId, HttpContext.RequestAborted);

        var stubs = holdings.Tickets
            .Select(t => TicketStubInfo.From(t, earlyEntry?.EarliestEntryDate))
            .ToList();

        return View(new TicketHoldingsViewModel(userId, viewMode, holdings.OrderCount, stubs));
    }
}

internal sealed record TicketHoldingsViewModel(
    Guid UserId, ProfileCardViewMode ViewMode, int OrderCount, IReadOnlyList<TicketStubInfo> Tickets, bool ChipOnly = false);
