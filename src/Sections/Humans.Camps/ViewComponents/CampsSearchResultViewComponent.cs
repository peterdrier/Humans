using Humans.Camps.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>
/// One camp-search result row, keyed by camp id
/// (nobodies-collective/Humans#1062). Callers hold the id their own search
/// produced; Camps owns what a camp row looks like, resolves the public-year
/// season name itself and fetches the slug the row links to. Replaces the
/// projection <c>SearchService</c> used to build.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal
/// view component ships <c>&lt;vc:…&gt;</c> as inert markup on a green build
/// (HUM0034's framework exception).
/// </remarks>
public sealed class CampsSearchResultViewComponent(ICampServiceRead camps) : ViewComponent
{
    /// <param name="campId">The matched camp.</param>
    public async Task<IViewComponentResult> InvokeAsync(Guid campId)
    {
        // Both reads are served from the cached CampInfo snapshot — one row costs no query.
        var camp = await camps.GetCampByIdAsync(campId);
        if (camp is null)
            return Content(string.Empty);

        var settings = await camps.GetSettingsAsync();
        var season = camp.GetSeasonForYear(settings.PublicYear);
        return View(new CampsSearchResultViewModel(season?.Name ?? camp.Slug, camp.Slug));
    }
}

internal sealed record CampsSearchResultViewModel(string Name, string Slug);
