using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class AccessMatrixViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(string section)
    {
        AccessMatrixDefinitions.Sections.TryGetValue(section, out var accessMatrix);

        var guide = SectionHelpContent.GetGuide(section);
        var glossary = SectionHelpContent.GetGlossary(section);

        // If no content at all, render nothing
        if (accessMatrix is null && guide is null && glossary is null)
            return Content(string.Empty);

        var model = new SectionHelpViewModel
        {
            SectionKey = section,
            SectionName = accessMatrix?.SectionName ?? section,
            GuideMarkdown = guide,
            GlossaryMarkdown = glossary,
            AccessMatrix = accessMatrix
        };

        return View(model);
    }
}
