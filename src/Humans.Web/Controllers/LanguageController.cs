using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Entities;

namespace Humans.Web.Controllers;

public class LanguageController : Controller
{
    private static readonly HashSet<string> SupportedCultures = new(StringComparer.Ordinal)
    {
        "en", "es", "de", "it", "fr"
    };

    private readonly UserManager<User> _userManager;

    public LanguageController(UserManager<User> userManager)
    {
        _userManager = userManager;
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
            var user = await _userManager.GetUserAsync(User);
            if (user != null)
            {
                user.PreferredLanguage = culture;
                await _userManager.UpdateAsync(user);
            }
        }

        return LocalRedirect(returnUrl ?? "/");
    }
}
