using System.Security.Claims;
using Humans.Onboarding.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Onboarding.ViewComponents;

internal sealed class OnboardingProgressBannerViewComponent(
    IOnboardingWidgetState state,
    ILogger<OnboardingProgressBannerViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;

        if (User.Identity?.IsAuthenticated != true)
            return View(false);

        var userIdRaw = UserClaimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdRaw, out var userId))
            return View(false);

        var path = HttpContext.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/OnboardingWidget", StringComparison.OrdinalIgnoreCase))
            return View(false);

        // The banner is advisory and must never break the layout, so a failed state lookup is swallowed.
        OnboardingWidgetStep step;
        try
        {
            step = await state.GetCurrentStepAsync(userId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OnboardingProgressBanner: GetCurrentStepAsync failed for user {UserId}; suppressing banner", userId);
            return View(false);
        }

        if (step == OnboardingWidgetStep.Complete)
            return View(false);

        return View(true);
    }
}
