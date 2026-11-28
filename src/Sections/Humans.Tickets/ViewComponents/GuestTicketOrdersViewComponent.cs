using Humans.Base.Extensions;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>Renders nothing for an account with no ticket and no order.</summary>
public sealed class GuestTicketOrdersViewComponent(
    ITicketService ticketService, ILogger<GuestTicketOrdersViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal.GetUserId() is not { } userId)
        {
            return Content(string.Empty);
        }

        var ct = HttpContext.RequestAborted;

        UserTicketHoldings holdings;
        try
        {
            holdings = await ticketService.GetUserTicketHoldingsAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
