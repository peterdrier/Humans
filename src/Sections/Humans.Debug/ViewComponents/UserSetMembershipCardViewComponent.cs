using Humans.Email.Contracts;
using Humans.Debug.Services;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Debug.ViewComponents;

/// <summary>Internal: invoked by type through <c>ChromeSlotViewComponent</c>, not a <c>&lt;vc:&gt;</c> tag helper.</summary>
internal sealed class UserSetMembershipCardViewComponent(
    IUserService userService,
    ISettingsService settingsService,
    IShiftView shiftView,
    ITicketService tickets,
    ICommunicationPreferenceService preferences) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var snapshot = await userService.GetAllUserInfosAsync(ct);
        var membership = await UserSetMembershipCalculator.BuildAsync(snapshot, settingsService, shiftView, tickets, preferences, ct);
        return membership.TotalUsers == 0 ? Content(string.Empty) : View(membership);
    }
}
