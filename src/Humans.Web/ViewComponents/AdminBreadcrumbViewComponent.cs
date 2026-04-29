using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed record AdminBreadcrumbViewModel(string? GroupLabel, string? ItemLabel, string? FallbackTitle);

public sealed class AdminBreadcrumbViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var controller = (string?)RouteData.Values["controller"];
        var action = (string?)RouteData.Values["action"];
        foreach (var group in AdminNavTree.Groups)
        {
            foreach (var item in group.Items)
            {
                if (string.Equals(item.Controller, controller, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Action, action, StringComparison.OrdinalIgnoreCase))
                    return View(new AdminBreadcrumbViewModel(group.Label, item.Label, null));
            }
        }
        var title = ViewData["Title"] as string;
        return View(new AdminBreadcrumbViewModel(null, null, title));
    }
}
