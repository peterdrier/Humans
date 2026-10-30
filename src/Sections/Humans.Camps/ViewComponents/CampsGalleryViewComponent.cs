using Humans.Camps.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>
/// The widget gallery's Camps samples. The search-result row renders nothing for an unresolved key, so it is keyed
/// off the first real camp of the public year; null when the environment holds none.
/// </summary>
internal sealed class CampsGalleryViewComponent(ICampService campService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var settings = await campService.GetSettingsAsync(ct);
        var camps = await campService.GetCampsForYearAsync(settings.PublicYear, ct);
        return View(new CampsGalleryViewModel(camps.FirstOrDefault()?.Id));
    }
}

internal sealed record CampsGalleryViewModel(Guid? CampId);
