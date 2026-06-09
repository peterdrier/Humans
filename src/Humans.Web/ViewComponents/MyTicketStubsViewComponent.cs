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
/// nothing when the holder has no visible tickets.
/// </summary>
public sealed class MyTicketStubsViewComponent(
    ITicketTransferService transferService,
    IEarlyEntryService earlyEntryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        // GetMyAttendeesAsync also returns attendees on orders this account *bought*,
        // even when those tickets are owned by the attendee's own account. The homepage
        // strip is "tickets you hold", so show only the ones this account currently owns,
        // matching the owner-filtered holdings list (GetUserTicketHoldingsAsync).
        var rows = (await transferService.GetMyAttendeesAsync(userId, HttpContext.RequestAborted))
            .Where(r => r.IsCurrentOwner)
            .ToList();
        if (rows.Count == 0) return Content(string.Empty);

        var earlyEntry = await earlyEntryService.GetForUserAsync(userId, HttpContext.RequestAborted);
        var stubs = rows
            .Select(r => TicketStubInfo.From(r, earlyEntry?.EarliestEntryDate))
            .ToList();

        return View(stubs);
    }
}
