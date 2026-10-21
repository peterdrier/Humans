using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// The "Onsite since {time}" chip (nobodies-collective/Humans#736), contributed to
/// <c>UserPartSlots.Profile</c>. Shown for the active event year once the member is Attended with
/// a gate check-in. Audience: the member on their own profile (<see cref="ProfileCardViewMode.Self"/>),
/// or a viewer passing <see cref="PolicyNames.TicketAdminBoardOrAdmin"/>, the policy gating
/// <c>/Tickets/Admin/Onsite</c>. Nothing for anyone else.
/// </summary>
public sealed class OnsiteChipViewComponent(
    ITicketService tickets,
    ISettingsService settingsService,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Self
            && !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.TicketAdminBoardOrAdmin)).Succeeded)
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
