using System.Security.Claims;
using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Humans.Web.Extensions;

namespace Humans.Web.Controllers;

public class LanguageController(IUserService userService) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLanguage(string culture, string? returnUrl, CancellationToken ct)
    {
        if (!culture.IsSupportedCultureCode())
        {
            culture = CultureCatalog.DefaultCultureCode;
        }

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });

        if (User.Identity?.IsAuthenticated == true
            && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await userService.SetPreferredLanguageAsync(userId, culture, ct);
        }

        return Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl!)
            : Redirect(Url.Content("~/"));
    }
}
