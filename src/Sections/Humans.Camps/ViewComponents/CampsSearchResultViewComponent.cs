using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>Camps owns what a camp row looks like: it resolves the active event's season name and the slug itself.</summary>
/// <remarks>
/// Public: Razor's discovery only sees public view components; an internal one ships as inert markup.
/// </remarks>
public sealed class CampsSearchResultViewComponent(ICampService camps) : ViewComponent
{
    /// <summary>Renders the row, or nothing when the camp no longer resolves.</summary>
    /// <param name="campId">The matched camp.</param>
    public async Task<IViewComponentResult> InvokeAsync(Guid campId)
    {
        // Both reads are served from the cached CampInfo snapshot — one row costs no query.
        var camp = await camps.GetCampByIdAsync(campId, HttpContext.RequestAborted);
        if (camp is null)
            return Content(string.Empty);

        var activeEventId = (await camps.GetSettingsAsync(HttpContext.RequestAborted)).ActiveEventSettingsId;
        var season = activeEventId is { } eventId ? camp.GetSeasonForEvent(eventId) : null;
        return View(new CampsSearchResultViewModel(season?.Name ?? camp.Slug, camp.Slug));
    }
}

internal sealed record CampsSearchResultViewModel(string Name, string Slug);
