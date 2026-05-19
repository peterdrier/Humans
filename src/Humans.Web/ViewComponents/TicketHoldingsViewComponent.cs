using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketHoldingsViewComponent(ITicketQueryService queryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool showEmpty = false)
    {
        var holdings = await queryService.GetUserTicketHoldingsAsync(userId);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.Tickets.Count == 0)
            return Content(string.Empty);

        return View(new TicketHoldingsViewModel(holdings.OrderCount, holdings.Tickets));
    }
}

public sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<UserTicketHoldingRow> Tickets);
