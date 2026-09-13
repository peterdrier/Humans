using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Models;
using Humans.Users.Contracts;

namespace Humans.Onboarding.Controllers;

/// <summary>
/// Dashboard for profileless accounts (authenticated users without a Profile). Comms
/// preferences, GDPR tools and ticket status are contributed by their own sections into
/// this page's cards / chrome slot.
/// </summary>
[Authorize]
internal sealed class GuestController(
    IUserServiceRead userService,
    IOnboardingWidgetState widgetState) : HumansControllerBase(userService)
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
        {
            return Challenge();
        }

        var step = await widgetState.GetCurrentStepAsync(user.Id, cancellationToken);
        if (step != OnboardingWidgetStep.Complete)
        {
            return RedirectToAction("Index", "OnboardingWidget");
        }

        return View(BuildDashboardViewModel(user));
    }

    private static GuestDashboardViewModel BuildDashboardViewModel(UserInfo user)
    {
        var viewModel = new GuestDashboardViewModel
        {
            DisplayName = user.BurnerName,
        };

        viewModel.IsDeletionPending = user.IsDeletionPending;
        viewModel.DeletionRequestedAt = user.DeletionRequestedAt?.ToDateTimeUtc();
        viewModel.DeletionScheduledFor = user.DeletionScheduledFor?.ToDateTimeUtc();
        viewModel.DeletionEligibleAfter = user.DeletionEligibleAfter?.ToDateTimeUtc();

        return viewModel;
    }
}
