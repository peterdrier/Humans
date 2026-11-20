using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.CityPlanning.ViewComponents;

/// <summary>
/// Every viewer who reaches this tab may edit (its gate is <c>CityPlanningMapAdmin</c>), so there is no read-only
/// branch.
/// </summary>
internal sealed class CityPlanningSettingsTabViewComponent(ICityPlanningService cityPlanningService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() =>
        View(new CityPlanningSettingsTabViewModel(await cityPlanningService.GetSettingsAsync(HttpContext.RequestAborted)));
}

/// <summary><c>Settings</c> is null when no event is active.</summary>
internal sealed record CityPlanningSettingsTabViewModel(CityPlanningSettingsDto? Settings);
