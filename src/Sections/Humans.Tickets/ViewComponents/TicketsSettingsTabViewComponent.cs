using Humans.Tickets.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary><see cref="Section"/> gates the tab on TicketsPolicies.Manage, so every viewer who reaches it may edit.</summary>
internal sealed class TicketsSettingsTabViewComponent(ITicketSyncService sync) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() =>
        View(new TicketsSettingsTabModel(
            await sync.IsScheduledSyncEnabledAsync(HttpContext.RequestAborted),
            await sync.GetPostEventHoldDaysAsync(HttpContext.RequestAborted)));
}

internal sealed record TicketsSettingsTabModel(bool IsScheduledSyncEnabled, int PostEventHoldDays);
