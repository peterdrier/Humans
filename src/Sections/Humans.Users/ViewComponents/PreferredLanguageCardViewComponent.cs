using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>The admin dashboard's "Preferred language" card: active users not pending deletion.</summary>
/// <remarks>
/// Internal: invoked by Type through <c>ChromeSlotViewComponent</c>, not a <c>&lt;vc:&gt;</c> tag helper.
/// </remarks>
internal sealed class PreferredLanguageCardViewComponent(
    IUserService userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var snapshot = await userService.GetAllUserInfosAsync(ct);
        var languages = PreferredLanguageCalculator.Build(snapshot)
            .OrderByDescending(language => language.Count)
            .ToList();
        return languages.Count == 0 ? Content(string.Empty) : View(languages);
    }
}
