using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>Stamps each stub with the holder's own Early Entry; renders nothing when they hold no tickets.</summary>
public sealed class MyTicketStubsViewComponent(
    ITicketService ticketService,
    IEarlyEntryService earlyEntryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var holdings = await ticketService.GetUserTicketHoldingsAsync(userId, HttpContext.RequestAborted);
        if (holdings.Tickets.Count == 0) return Content(string.Empty);

        var earlyEntry = await earlyEntryService.GetForUserAsync(userId, HttpContext.RequestAborted);
        var stubs = holdings.Tickets
            .Select(t => TicketStubInfo.From(t, earlyEntry?.EarliestEntryDate))
            .ToList();

        return View(stubs);
    }
}
