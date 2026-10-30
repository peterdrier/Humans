using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>Every viewer who reaches this tab may edit: <see cref="Section"/> gates it on CampsPolicies.Manage.</summary>
internal sealed class CampBarriosSettingsTabViewComponent(ICampManagement campService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var events = await campService.GetRegistrationByEventAsync(HttpContext.RequestAborted);
        return View(new CampBarriosSettingsViewModel(events));
    }
}
