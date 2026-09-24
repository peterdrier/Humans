using Humans.Base.Authorization;
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
        var groups = await AdminNavItems.ForRequestAsync(this, navContributors, authorization, environment, serviceProvider, logger);

        // A restricted page puts non-admin roles (a team coordinator on the shift dashboard) in the
        // shell too; the dashboard itself is AnyAdminRole-only.
        var showDashboard = (await authorization.AuthorizeAsync(HttpContext.User, PolicyNames.AnyAdminRole)).Succeeded;
        return View(new AdminSidebarViewModel(showDashboard, groups));
    }
}
