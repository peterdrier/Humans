using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Base.ViewComponents;

/// <summary>
/// The admin top bar's section name: the nav group the current route belongs to
/// (<see cref="AdminNavComposition.Locate"/>), else the page's <c>ViewData["Title"]</c> off the nav.
/// </summary>
public sealed class AdminSectionNameViewComponent(
    IEnumerable<ISectionAdminNav> navContributors) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var location = AdminNavComposition.Locate(AdminNavComposition.Compose(navContributors),
            (string?)RouteData.Values["controller"], (string?)RouteData.Values["action"],
            ViewData[AdminNavComposition.ParentKey] as string, RouteData.Values);

        return Content(location?.Group.Label ?? ViewData["Title"] as string ?? string.Empty);
    }
}
