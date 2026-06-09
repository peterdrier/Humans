using Humans.Application.DTOs;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketHoldingsViewComponent(
    ITicketServiceRead queryService,
    IEarlyEntryService earlyEntryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool showEmpty = false)
    {
        var holdings = await queryService.GetUserTicketHoldingsAsync(userId);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.Tickets.Count == 0)
            return Content(string.Empty);

        // The holder's own EE (earliest date across sources), shown on each of their stubs.
        var earlyEntry = await earlyEntryService.GetForUserAsync(userId, HttpContext.RequestAborted);

        var stubs = holdings.Tickets
            .Select(t => TicketStubInfo.From(t, earlyEntry?.EarliestEntryDate))
            .ToList();

        return View(new TicketHoldingsViewModel(holdings.OrderCount, stubs));
    }
}

public sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<TicketStubInfo> Tickets);
