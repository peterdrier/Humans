using System.Security.Claims;
using Humans.Onboarding.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Onboarding.ViewComponents;

internal sealed record OnboardingProgressBannerViewModel(bool Show);

internal sealed class OnboardingProgressBannerViewComponent(
    IOnboardingWidgetState state,
    ILogger<OnboardingProgressBannerViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var hidden = new OnboardingProgressBannerViewModel(Show: false);
        var ct = HttpContext.RequestAborted;

        if (User?.Identity?.IsAuthenticated != true)
            return View(hidden);

        var principal = User as ClaimsPrincipal;
        var userIdRaw = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdRaw, out var userId))
            return View(hidden);

        var path = HttpContext.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/OnboardingWidget", StringComparison.OrdinalIgnoreCase))
            return View(hidden);

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
            return View(hidden);
        }

        if (step == OnboardingWidgetStep.Complete)
            return View(hidden);

        return View(new OnboardingProgressBannerViewModel(Show: true));
    }
}
