using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.ViewComponents;

/// <summary>A contributor that throws is logged and skipped, so one section cannot break the page.</summary>
// Public: Razor's tag-helper discovery only sees public view components; an internal one renders as inert markup.
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
