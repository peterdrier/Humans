using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin sidebar: one row per group the user can see anything in, alphabetical, linking to
/// the group's first visible item. The group's items render as tabs on its pages
/// (<see cref="AdminTabsViewComponent"/>).
/// </summary>
public sealed class AdminSidebarViewComponent(
    IAuthorizationService authorization,
    IWebHostEnvironment environment,
    IServiceProvider serviceProvider,
    IEnumerable<ISectionAdminNav> navContributors,
    ILogger<AdminSidebarViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var groups = AdminNavComposition.Compose(navContributors);
        var location = AdminNavComposition.Locate(groups,
            (string?)RouteData.Values["controller"], (string?)RouteData.Values["action"],
            ViewData[AdminNavComposition.ParentKey] as string);

        var rows = new List<AdminSidebarGroupViewModel>(groups.Count);
        foreach (var group in groups)
        {
            var items = await AdminNavItems.VisibleAsync(group, location?.Item, HttpContext.User,
                authorization, environment, serviceProvider, logger);
            if (items.Count > 0)
                rows.Add(new AdminSidebarGroupViewModel(group.Label, items, ReferenceEquals(group, location?.Group)));
        }

        return View(new AdminSidebarViewModel(rows));
    }
}
