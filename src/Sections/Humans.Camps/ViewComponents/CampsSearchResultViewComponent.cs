using Humans.Camps.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>
/// One camp-search result row, keyed by slug
/// (nobodies-collective/Humans#1062). Callers hold the key their own search
/// produced; Camps owns what a camp row looks like and resolves the public-year
/// season name itself. Replaces the projection <c>SearchService</c> used to build.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal
/// view component ships <c>&lt;vc:…&gt;</c> as inert markup on a green build
/// (HUM0034's framework exception). Slug rather than id: <c>CampSearchHit</c>
/// carries no id, and the slug is already the camp's canonical URL key.
/// </remarks>
public sealed class CampsSearchResultViewComponent(ICampServiceRead camps) : ViewComponent
{
    /// <param name="slug">The matched camp's URL slug.</param>
    public async Task<IViewComponentResult> InvokeAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Content(string.Empty);

        // Both reads are served from the cached CampInfo snapshot — one row costs no query.
        var camp = await camps.GetCampBySlugAsync(slug);
        if (camp is null)
            return Content(string.Empty);

        var settings = await camps.GetSettingsAsync();
        var season = camp.GetSeasonForYear(settings.PublicYear);
        return View(new CampsSearchResultViewModel(season?.Name ?? camp.Slug, camp.Slug));
    }
}

internal sealed record CampsSearchResultViewModel(string Name, string Slug);
