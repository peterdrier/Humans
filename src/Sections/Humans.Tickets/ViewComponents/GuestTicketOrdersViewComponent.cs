using System.Security.Claims;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// The profileless-account page's ticket card: the orders the account bought. Renders
/// nothing for an account with no ticket and no order.
/// </summary>
public sealed class GuestTicketOrdersViewComponent(
    ITicketServiceRead ticketService, ILogger<GuestTicketOrdersViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var ct = HttpContext.RequestAborted;

        UserTicketHoldings holdings;
        try
        {
            holdings = await ticketService.GetUserTicketHoldingsAsync(userId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // request aborted — let it abort, don't log as an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load ticket orders for guest dashboard, user {UserId}", userId);
            return Content(string.Empty);
        }

        if (!holdings.HasTicketAttendeeMatch)
        {
            return Content(string.Empty);
        }

        return View(holdings.OrderSummaries);
    }
}
