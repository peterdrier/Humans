using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Base.ViewComponents;

/// <param name="Group">The group label; null off the nav, where only <paramref name="Title"/> renders.</param>
/// <param name="Page">The nav item the route is, or is a subpage of.</param>
/// <param name="Trail">A subpage's own crumbs after <paramref name="Page"/>, itself last (its <c>Crumbs</c> section); replaces <paramref name="Title"/>.</param>
/// <param name="Title">The page's own title — the last crumb on a subpage without a trail, or off the nav.</param>
public sealed record AdminBreadcrumbViewModel(
    string? Group, AdminNavItem? Page, bool IsSubpage, IHtmlContent? Trail, string? Title)
{
    /// <summary>A single-item group named like its item ("Holded / Holded") states the label once.</summary>
    public bool PageRepeatsGroup => Page is not null && string.Equals(Page.Label, Group, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// <c>&lt;Group&gt; / &lt;Page&gt; / &lt;Subpage&gt;</c> for the current admin route. The group and page
/// come from the nav (<see cref="AdminNavComposition.Locate"/>); a subpage adds its
/// <c>ViewData["Title"]</c>, or the crumbs of its <c>Crumbs</c> section when it needs more than
/// one level or a last crumb that is not its title.
/// </summary>
public sealed class AdminBreadcrumbViewComponent(
    IEnumerable<ISectionAdminNav> navContributors) : ViewComponent
{
    public IViewComponentResult Invoke(IHtmlContent? trail = null)
    {
        var title = ViewData["Title"] as string;
        var location = AdminNavComposition.Locate(AdminNavComposition.Compose(navContributors),
            (string?)RouteData.Values["controller"], (string?)RouteData.Values["action"],
            ViewData[AdminNavComposition.ParentKey] as string);

        return View(location is null
            ? new AdminBreadcrumbViewModel(null, null, IsSubpage: false, null, title)
            : new AdminBreadcrumbViewModel(location.Group.Label, location.Item, !location.IsExact, trail, title));
    }
}
