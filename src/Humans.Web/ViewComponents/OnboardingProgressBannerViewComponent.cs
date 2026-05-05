using System.Security.Claims;
using Humans.Application.Interfaces.Onboarding;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed record OnboardingProgressBannerViewModel(bool Show);

public sealed class OnboardingProgressBannerViewComponent : ViewComponent
{
    private readonly IOnboardingWidgetState _state;
    private readonly ILogger<OnboardingProgressBannerViewComponent> _logger;

    public OnboardingProgressBannerViewComponent(
        IOnboardingWidgetState state,
        ILogger<OnboardingProgressBannerViewComponent> logger)
    {
        _state = state;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var hidden = new OnboardingProgressBannerViewModel(Show: false);

        if (User?.Identity?.IsAuthenticated != true)
            return View(hidden);

        var principal = User as ClaimsPrincipal;
        var userIdRaw = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdRaw, out var userId))
            return View(hidden);

        // Don't show the "Continue setup →" prompt on widget pages themselves —
        // the user is already inside the onboarding flow and doesn't need a
        // banner offering to send them where they already are.
        var path = HttpContext.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/OnboardingWidget", StringComparison.OrdinalIgnoreCase))
            return View(hidden);

        // Banner is purely advisory — it must never break the layout. If state lookup
        // throws (DbContext disposed mid-render, transient query failure, half-logout
        // cookie state, etc.) swallow the error so the page still renders.
        OnboardingWidgetStep step;
        try
        {
            step = await _state.GetCurrentStepAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnboardingProgressBanner: GetCurrentStepAsync failed for user {UserId}; suppressing banner", userId);
            return View(hidden);
        }

        if (step == OnboardingWidgetStep.Complete)
            return View(hidden);

        return View(new OnboardingProgressBannerViewModel(Show: true));
    }
}
