using Humans.Application.DTOs;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders the current holder's ticket stubs as a bare flex-wrap strip (no card
/// chrome), stamping each with the holder's Early Entry date. The single home for
/// "my tickets as stubs" on the homepage dashboard; the transfer wizard shares the
/// same <see cref="TicketStubInfo.From"/> mapper for its selectable list. Renders
/// nothing when the holder owns no tickets.
/// </summary>
public sealed class MyTicketStubsViewComponent(
    ITicketServiceRead ticketService,
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
