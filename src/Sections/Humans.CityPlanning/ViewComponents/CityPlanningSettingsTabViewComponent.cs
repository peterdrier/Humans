using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.CityPlanning.ViewComponents;

/// <summary>
/// Every viewer who reaches this tab may edit (its gate is <c>CityPlanningMapAdmin</c>), so there is no read-only
/// branch.
/// </summary>
internal sealed class CityPlanningSettingsTabViewComponent(
    ICityPlanningService cityPlanningService,
    ICampService campService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var settings = await cityPlanningService.GetSettingsAsync(ct);
        // Keyed to the highest open season, not settings.Year.
        var registrationInfo = await cityPlanningService.GetRegistrationInfoAsync(ct);
        // Editable whenever it has a year to write to: an open season, else the active event.
        var registrationInfoEditable = settings is not null
            || (await campService.GetSettingsAsync(ct)).OpenSeasons.Count > 0;

        return View(new CityPlanningSettingsTabViewModel(settings, registrationInfo, registrationInfoEditable));
    }
}

/// <summary>
/// <c>Settings</c> is null when no event is active. The registration-info card shows whenever
/// <c>RegistrationInfoEditable</c>, so an open season stays editable without an active event.
/// </summary>
internal sealed record CityPlanningSettingsTabViewModel(
    CityPlanningSettingsDto? Settings, string? RegistrationInfo, bool RegistrationInfoEditable);
