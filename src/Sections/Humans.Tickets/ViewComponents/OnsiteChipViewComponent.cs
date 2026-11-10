using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// Only for the member on their own profile or a viewer passing <see cref="TicketsPolicies.Read"/>. On the member's own
/// profile it renders only inside the ticket holdings card, not as its own profile part.
/// </summary>
public sealed class OnsiteChipViewComponent(
    ITicketService tickets,
    ISettingsService settingsService,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode, bool inHoldings = false)
    {
        if (viewMode == ProfileCardViewMode.Self && !inHoldings)
            return Content(string.Empty);

        if (viewMode != ProfileCardViewMode.Self
            && !(await authorization.AuthorizeAsync(UserClaimsPrincipal, TicketsPolicies.Read)).Succeeded)
            return Content(string.Empty);

        var ct = HttpContext.RequestAborted;
        var active = await settingsService.GetActiveEventSettingsAsync(ct);
        if (active is null || active.Year == 0)
            return Content(string.Empty);

        var onsiteSince = (await tickets.GetParticipationsAsync(userId, ct))
            .FirstOrDefault(p => p.Year == active.Year)?.OnsiteSince;
        return onsiteSince is { } since ? View(since) : Content(string.Empty);
    }
}
