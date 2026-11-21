using Humans.Tickets.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary><see cref="Section"/> gates the tab on TicketsPolicies.Manage, so every viewer who reaches it may edit.</summary>
internal sealed class TicketsSettingsTabViewComponent(ITicketSyncService sync) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() =>
        View(await sync.IsScheduledSyncEnabledAsync(HttpContext.RequestAborted));
}
