using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed record AdminBreadcrumbViewModel(string? SectionLabel, string? ItemLabel, string? FallbackTitle);

/// <summary>
/// <c>Admin / &lt;Section&gt; / &lt;Page&gt;</c> for the current admin route.
/// </summary>
/// <remarks>
/// The middle crumb is the <em>owning section</em>, not the sidebar's merge group: "Money" holds
/// items from Expenses, Budget, Finance and Holded, so it answers "which drawer is this in", not
/// "where am I". The section is derived from the contributing <see cref="ISectionAdminNav"/>'s
/// assembly rather than declared, so a new section gets a correct crumb with no Shell edit —
/// which is also why this walks the contributors itself instead of
/// <see cref="AdminNavComposition.Compose"/>, whose whole job is to merge that attribution away.
/// </remarks>
public sealed class AdminBreadcrumbViewComponent(
    IEnumerable<ISectionAdminNav> navContributors) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var controller = (string?)RouteData.Values["controller"];
        var action = (string?)RouteData.Values["action"];

        foreach (var contributor in navContributors)
        {
            foreach (var item in contributor.Groups().SelectMany(g => g.Items))
            {
                if (string.Equals(item.Controller, controller, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Action, action, StringComparison.OrdinalIgnoreCase))
                    return View(new AdminBreadcrumbViewModel(
                        SectionLabel(contributor), item.CrumbLabel, null));
            }
        }

        var title = ViewData["Title"] as string;
        return View(new AdminBreadcrumbViewModel(null, null, title));
    }

    /// <summary>
    /// "Humans.Expenses" → "Expenses". Every <see cref="ISectionAdminNav"/> lives in its own
    /// section project, so the assembly name is the section name.
    /// </summary>
    private static string SectionLabel(ISectionAdminNav contributor)
    {
        var assembly = contributor.GetType().Assembly.GetName().Name ?? "";
        return assembly.StartsWith("Humans.", StringComparison.Ordinal) ? assembly[7..] : assembly;
    }
}
