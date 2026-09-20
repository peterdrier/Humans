using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.CityPlanning.ViewComponents;

/// <summary>
/// The /Settings#city-planning tab (peterdrier/Humans#1634): placement windows,
/// scheduled times, and registration info. <see cref="SectionSettings"/> gates it on
/// <c>PolicyNames.CampAdminOrAdmin</c>, so every viewer who reaches this component may
/// edit — no read-only branch, unlike <c>EventSettingsTabViewComponent</c>.
/// </summary>
internal sealed class CityPlanningSettingsTabViewComponent(ICityPlanningServiceRead cityPlanningService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var settings = await cityPlanningService.GetSettingsAsync(HttpContext.RequestAborted);
        // Registration info is keyed to the highest open season, not settings.Year — see
        // ICityPlanningServiceRead.GetRegistrationInfoAsync.
        var registrationInfo = await cityPlanningService.GetRegistrationInfoAsync(HttpContext.RequestAborted);

        return View(new CityPlanningSettingsTabViewModel(settings, registrationInfo));
    }
}

internal sealed record CityPlanningSettingsTabViewModel(
    CityPlanningSettingsDto Settings, string? RegistrationInfo);
