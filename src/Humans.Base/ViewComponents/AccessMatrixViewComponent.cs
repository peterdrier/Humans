using Humans.Base.Interfaces;
using Humans.Base.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Base.ViewComponents;

public class AccessMatrixViewComponent(IEnumerable<ISectionHelp> helpContributions) : ViewComponent
{
    public IViewComponentResult Invoke(string section)
    {
        AccessMatrixDefinitions.Sections.TryGetValue(section, out var accessMatrix);

        // Ordinal, like the dictionaries this replaced: the key is the one the call site spells.
        var help = helpContributions
            .SelectMany(c => c.HelpEntries)
            .FirstOrDefault(e => string.Equals(e.Key, section, StringComparison.Ordinal));
        var guide = help?.Guide;
        var glossary = help?.Glossary;

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
