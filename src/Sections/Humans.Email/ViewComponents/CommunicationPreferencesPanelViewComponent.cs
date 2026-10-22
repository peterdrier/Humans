using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Email.ViewComponents;

public sealed class CommunicationPreferencesPanelViewComponent(
    ICommunicationPreferenceService commPrefService,
    ITicketService ticketQueryService,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool readOnly = false)
    {
        var ct = HttpContext.RequestAborted;
        var prefs = await commPrefService.GetPreferencesReadOnlyAsync(userId, ct);
        var hasTicketOrder = (await ticketQueryService.GetUserTicketHoldingsAsync(userId, ct))
            .HasTicketAttendeeMatch;

        return View(new CommunicationPreferencesViewModel
        {
            Categories = CategoryPreferenceItem.ForActiveCategories(prefs, hasTicketOrder),
            ReadOnly = readOnly,
            TicketingYear = clock.GetCurrentInstant().InUtc().Year,
        });
    }
}
