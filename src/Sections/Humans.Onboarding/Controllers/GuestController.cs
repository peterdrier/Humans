using Humans.Base.Attributes;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Onboarding.Services;
using Humans.Onboarding.Models;
using Humans.Users.Contracts;

namespace Humans.Onboarding.Controllers;

[Authorize]
[AllowBeforeActiveMembership]
internal sealed class GuestController(
    IUserService userService,
    IOnboardingWidgetState widgetState) : HumansControllerBase(userService)
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserInfoAsync(cancellationToken);
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
