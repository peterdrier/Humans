using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.CityPlanning.ViewComponents;

/// <summary>
/// The /Settings#city-planning tab (peterdrier/Humans#1634): placement windows,
/// scheduled times, and registration info. <see cref="SectionSettings"/> gates it on
/// <c>PolicyNames.CityPlanningMapAdmin</c> (Admin/CampAdmin or a city-planning team
/// member), so every viewer who reaches this component may edit — no read-only branch,
/// unlike <c>EventSettingsTabViewComponent</c>.
/// </summary>
internal sealed class CityPlanningSettingsTabViewComponent(
    ICityPlanningService cityPlanningService,
    ICampService campService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var settings = await cityPlanningService.GetSettingsAsync(ct);
        // Registration info is keyed to the highest open season, not settings.Year — see
        // ICityPlanningService.GetRegistrationInfoAsync.
        var registrationInfo = await cityPlanningService.GetRegistrationInfoAsync(ct);
        // Editable whenever it has a year to write to: an open season, else the active event.
        var registrationInfoEditable = settings is not null
            || (await campService.GetSettingsAsync(ct)).OpenSeasons.Count > 0;

        return View(new CityPlanningSettingsTabViewModel(settings, registrationInfo, registrationInfoEditable));
    }
}

/// <summary>
/// The tab's model. <c>Settings</c> is null when no event is active — the tab then says so in place
/// of the placement cards. The registration-info card shows whenever <c>RegistrationInfoEditable</c>,
/// so an open season stays editable without an active event.
/// </summary>
internal sealed record CityPlanningSettingsTabViewModel(
    CityPlanningSettingsDto? Settings, string? RegistrationInfo, bool RegistrationInfoEditable);
