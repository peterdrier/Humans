using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Entities;

namespace Humans.Web.Controllers;

public class LanguageController : HumansControllerBase
{
    private static readonly HashSet<string> SupportedCultures = new(StringComparer.Ordinal)
    {
        "en", "es", "de", "it", "fr"
    };

    public LanguageController(UserManager<User> userManager)
        : base(userManager)
    {
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLanguage(string culture, string? returnUrl)
    {
        if (!SupportedCultures.Contains(culture))
        {
            culture = "en";
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
            if (user != null)
            {
                user.PreferredLanguage = culture;
                await UpdateCurrentUserAsync(user);
            }
        }

        return LocalRedirect(returnUrl ?? "/");
    }
}
