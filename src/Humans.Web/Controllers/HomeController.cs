using Humans.Base.Controllers;
using Microsoft.AspNetCore.Mvc;
using Humans.Base.Configuration;
using Humans.Web.Models;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Humans.Base.Authorization;

namespace Humans.Web.Controllers;

public class HomeController(
    IUserService userService,
    IConsentService consentService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    IAuthorizationService authorizationService) : HumansControllerBase(userService)
{
    // Public landing page and member dashboard on one route. MembershipRequiredFilter and NameRequiredFilter
    // still gate signed-in users here despite the attribute (see MembershipRequiredFilter.IsPublicEndpoint).
    [AllowAnonymous]
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

    [AllowAnonymous]
    public IActionResult Privacy()
    {
        ViewData["DpoEmail"] = configuration.GetOptionalSetting(
            configRegistry, "Email:DpoAddress", "Email", importance: ConfigurationImportance.Recommended);
        return View();
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [Route("/Home/Error/{statusCode?}")]
    public async Task<IActionResult> Error(int? statusCode = null)
    {
        if (statusCode == 404)
        {
            return View("Error404");
        }

        // Exception text can carry any section's data, so only roles that may already read the logs see it.
        var exception = HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error;
        var showDetail = exception is not null
            && (await authorizationService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded;

        return View(new ErrorViewModel(
            statusCode ?? Response.StatusCode,
            HttpContext.TraceIdentifier,
            showDetail ? exception!.ToString() : null));
    }
}
