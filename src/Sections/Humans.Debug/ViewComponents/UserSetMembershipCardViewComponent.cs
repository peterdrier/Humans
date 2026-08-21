using Humans.Debug.Services;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Debug.ViewComponents;

/// <summary>
/// The Venn + UpSet "user set membership" card on the admin dashboard. Contributed into the
/// admin dashboard's <c>admin-dashboard</c> chrome slot (<see cref="Humans.Debug.SectionChrome"/>)
/// since nobodies-collective/Humans#1091.
/// </summary>
/// <remarks>
/// Internal — invoked by name through <c>ChromeSlotViewComponent</c>'s
/// <c>Component.InvokeAsync</c>, not a <c>&lt;vc:&gt;</c> tag helper.
/// </remarks>
internal sealed class UserSetMembershipCardViewComponent(
    IUserServiceRead userService,
    IBurnSettingsService burnSettings,
    IShiftView shiftView) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var snapshot = await userService.GetAllUserInfosAsync(ct);
        var membership = await UserSetMembershipCalculator.BuildAsync(snapshot, burnSettings, shiftView, ct);
        return membership.TotalUsers == 0 ? Content(string.Empty) : View(membership);
    }
}
