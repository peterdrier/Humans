using Humans.Camps.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>
/// The /Settings#barrios tab (peterdrier/Humans#1634). <see cref="SectionSettings"/> gates
/// it on <c>PolicyNames.CampAdminOrAdmin</c> — the same policy the old <c>Camps/Admin</c>
/// Season Management card used, so every viewer who reaches this component may edit.
/// </summary>
internal sealed class CampBarriosSettingsTabViewComponent(ICampService campService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var settings = await campService.GetSettingsAsync(HttpContext.RequestAborted);
        return View(new CampBarriosSettingsViewModel(settings.OpenSeasons));
    }
}
