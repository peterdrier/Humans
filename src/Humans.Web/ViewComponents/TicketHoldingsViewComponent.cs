using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketHoldingsViewComponent : ViewComponent
{
    private readonly ITicketQueryService _queryService;

    public TicketHoldingsViewComponent(ITicketQueryService queryService)
    {
        _queryService = queryService;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool showEmpty = false)
    {
        var holdings = await _queryService.GetUserTicketHoldingsAsync(userId);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.AttendeeNames.Count == 0)
            return Content(string.Empty);

        return View(new TicketHoldingsViewModel(holdings.OrderCount, holdings.AttendeeNames));
    }
}

public sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<string> AttendeeNames);
