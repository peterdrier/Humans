using Humans.Base.Interfaces;
using Humans.Base.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Base.ViewComponents;

public class AccessMatrixViewComponent(
    IEnumerable<ISectionAccessMatrix> accessMatrices,
    IEnumerable<ISectionHelp> helpContributions) : ViewComponent
{
    public IViewComponentResult Invoke(string section)
    {
        // Ordinal: the key is the one the call site spells.
        var accessMatrix = accessMatrices
            .SelectMany(c => c.AccessMatrices)
            .FirstOrDefault(m => string.Equals(m.Key, section, StringComparison.Ordinal));

        // Ordinal: the key is the one the call site spells.
        var help = helpContributions
            .SelectMany(c => c.HelpEntries)
            .FirstOrDefault(e => string.Equals(e.Key, section, StringComparison.Ordinal));
        var guide = help?.Guide;
        var glossary = help?.Glossary;

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
