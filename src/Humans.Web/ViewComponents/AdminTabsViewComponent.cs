using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The tab strip above the breadcrumb: the current page's group's visible items, with the page
/// (or the item it is a subpage of) active. Renders nothing for a group with a single tab.
/// </summary>
public sealed class AdminTabsViewComponent(
    IAuthorizationService authorization,
    IWebHostEnvironment environment,
    IServiceProvider serviceProvider,
    IEnumerable<ISectionAdminNav> navContributors,
    ILogger<AdminTabsViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var location = AdminNavComposition.Locate(AdminNavComposition.Compose(navContributors),
            (string?)RouteData.Values["controller"], (string?)RouteData.Values["action"],
            ViewData[AdminNavComposition.ParentKey] as string);
        if (location is null)
            return Content(string.Empty);

        var tabs = await AdminNavItems.VisibleAsync(location.Group, location.Item, HttpContext.User,
            authorization, environment, serviceProvider, logger);
        return tabs.Count > 1 ? View(tabs) : Content(string.Empty);
    }
}
