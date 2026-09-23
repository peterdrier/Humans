using Humans.Camps.Contracts;
using Humans.Camps.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>
/// Renders every active section's <see cref="ICampPart"/> descriptors for the camp detail
/// page, ordered by weight, each invoked as its declared component <see cref="Type"/> with a
/// <see cref="CampPartArgs"/>. Mirrors Base's <c>UserPartsViewComponent</c>
/// (nobodies-collective/Humans#1815). A contributor that throws is logged and skipped so one
/// section cannot break the page.
/// </summary>
// Public, not internal: Razor's build-time tag-helper discovery only sees public view
// components, so an internal one renders <vc:camp-parts> as inert markup.
public sealed class CampPartsViewComponent(
    IEnumerable<ICampPart> contributors,
    ILogger<CampPartsViewComponent> logger) : ViewComponent
{
    public IViewComponentResult Invoke(Guid campId)
    {
        var parts = new List<CampPart>();
        foreach (var contributor in contributors)
        {
            try
            {
                parts.AddRange(contributor.Parts());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load camp parts from {Contributor}", contributor.GetType().Name);
            }
        }

        if (parts.Count == 0) return Content(string.Empty);

        return View("Default", new CampPartsViewModel(
            new CampPartArgs(campId),
            [.. parts.OrderBy(p => p.Weight).ThenBy(p => p.Component.FullName, StringComparer.Ordinal)]));
    }
}
