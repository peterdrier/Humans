using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed record AdminBreadcrumbViewModel(string? GroupLabelKey, string? ItemLabelKey, string? FallbackTitle);

public sealed class AdminBreadcrumbViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var controller = (string?)RouteData.Values["controller"];
        foreach (var group in AdminNavTree.Groups)
        {
            foreach (var item in group.Items)
            {
                if (string.Equals(item.Controller, controller, StringComparison.OrdinalIgnoreCase))
                    return View(new AdminBreadcrumbViewModel(group.LabelKey, item.LabelKey, null));
            }
        }
        var title = ViewData["Title"] as string;
        return View(new AdminBreadcrumbViewModel(null, null, title));
    }
}
