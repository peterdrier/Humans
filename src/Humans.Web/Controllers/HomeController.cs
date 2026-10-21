using Humans.Base.Controllers;
using Microsoft.AspNetCore.Mvc;
using Humans.Base.Configuration;
using Humans.Web.Models;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;

namespace Humans.Web.Controllers;

public class HomeController(
    IUserService userService,
    IConsentService consentService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry) : HumansControllerBase(userService)
{
    public async Task<IActionResult> Index()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return View();
        }

        var user = await GetCurrentUserInfoAsync();
        if (user is null)
        {
            return View();
        }

        var review = await consentService.GetConsentReviewAsync(user.Id);

        // Name-only access: the MembershipRequiredFilter routes non-Active users away, so any
        // authenticated user reaching the dashboard is Active (named, not suspended/etc.).
        var viewModel = new DashboardViewModel
        {
            UserId = user.Id,
            DisplayName = user.BurnerName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasProfile = user.Profile is not null,
            ProfileComplete = user.HasRequiredNameFields,
            IsRejected = user.Profile is not null && review.IsRejected,
            RejectionReason = user.Profile is null ? null : review.RejectionReason,
            MemberSince = user.CreatedAt.ToDateTimeUtc(),
            LastLogin = user.LastLoginAt?.ToDateTimeUtc(),
        };

        return View("Dashboard", viewModel);
    }

    public IActionResult Privacy()
    {
        ViewData["DpoEmail"] = configuration.GetOptionalSetting(
            configRegistry, "Email:DpoAddress", "Email", importance: ConfigurationImportance.Recommended);
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [Route("/Home/Error/{statusCode?}")]
    public IActionResult Error(int? statusCode = null)
    {
        if (statusCode == 404)
        {
            return View("Error404");
        }

        return View();
    }
}
