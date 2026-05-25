using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class AdminSidebarViewComponent(
    IAuthorizationService authorization,
    IWebHostEnvironment environment,
    IServiceProvider serviceProvider,
    IHttpContextAccessor httpContext,
    ILogger<AdminSidebarViewComponent> logger) : ViewComponent
{
    private readonly IHttpContextAccessor _httpContext = httpContext;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var activeController = (string?)RouteData.Values["controller"];
        var activeAction = (string?)RouteData.Values["action"];
        var visibleGroups = new List<AdminSidebarGroupViewModel>(AdminNavTree.Groups.Count);

        foreach (var group in AdminNavTree.Groups)
        {
            var visibleItems = new List<AdminSidebarItemViewModel>(group.Items.Count);
            foreach (var item in group.Items)
            {
                if (item.EnvironmentGate is not null && !item.EnvironmentGate(environment))
                    continue;

                if (item.Policy is not null)
                {
                    var auth = await authorization.AuthorizeAsync(HttpContext.User, null, item.Policy);
                    if (!auth.Succeeded) continue;
                }
                else if (item.RoleCheck is not null && !item.RoleCheck(HttpContext.User))
                {
                    continue;
                }

                int? pill = null;
                if (item.PillCount is not null)
                {
                    try
                    {
                        pill = await item.PillCount(serviceProvider);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to compute pill count for nav item {Label}", item.Label);
                        pill = null;
                    }
                }

                visibleItems.Add(new AdminSidebarItemViewModel(
                    Label: item.Label,
                    Controller: item.Controller,
                    Action: item.Action,
                    RouteValues: item.RouteValues,
                    RawHref: item.RawHref,
                    IconCssClass: item.IconCssClass,
                    IsActive: !string.IsNullOrEmpty(item.Controller)
                              && string.Equals(item.Controller, activeController, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(item.Action, activeAction, StringComparison.OrdinalIgnoreCase),
                    PillCount: pill));
            }

            if (visibleItems.Count > 0)
                visibleGroups.Add(new AdminSidebarGroupViewModel(group.Label, visibleItems));
        }

        return View(new AdminSidebarViewModel(visibleGroups));
    }
}
