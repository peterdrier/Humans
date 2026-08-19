using System.Security.Claims;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// The profileless-account page's ticket card: the orders the account bought. Renders
/// nothing for an account with no ticket and no order.
/// </summary>
public sealed class GuestTicketOrdersViewComponent(ITicketServiceRead ticketService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var holdings = await ticketService.GetUserTicketHoldingsAsync(userId, HttpContext.RequestAborted);
        if (!holdings.HasTicketAttendeeMatch)
        {
            return Content(string.Empty);
        }

        return View(holdings.OrderSummaries);
    }
}
