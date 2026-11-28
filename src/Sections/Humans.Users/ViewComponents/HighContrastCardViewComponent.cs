using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>The admin dashboard's "High contrast" card: the same members the language card counts.</summary>
/// <remarks>
/// Internal: invoked by Type through <c>ChromeSlotViewComponent</c>, not a <c>&lt;vc:&gt;</c> tag helper.
/// </remarks>
internal sealed class HighContrastCardViewComponent(
    IUserService userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var snapshot = await userService.GetAllUserInfosAsync(HttpContext.RequestAborted);
        return View(PreferredLanguageCalculator.CountHighContrast(snapshot));
    }
}
