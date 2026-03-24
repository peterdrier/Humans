using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Entities;
using Humans.Web.Extensions;

namespace Humans.Web.Controllers;

public class LanguageController : HumansControllerBase
{
    public LanguageController(UserManager<User> userManager)
        : base(userManager)
    {
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLanguage(string culture, string? returnUrl)
    {
        if (!culture.IsSupportedCultureCode())
        {
            culture = CultureCatalog.DefaultCultureCode;
        }

        // Set the culture cookie
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });

        // If authenticated, persist to user profile
        if (User.Identity?.IsAuthenticated == true)
        {
            var user = await GetCurrentUserAsync();
            if (user is not null)
            {
                user.PreferredLanguage = culture;
                await UpdateCurrentUserAsync(user);
            }
        }

        return LocalRedirect(returnUrl ?? "/");
    }
}
