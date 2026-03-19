using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class AccessMatrixViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(string section)
    {
        if (!AccessMatrixDefinitions.Sections.TryGetValue(section, out var data))
            return Content(string.Empty);

        return View(data);
    }
}
