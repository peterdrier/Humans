using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.Web.ViewComponents;

public sealed class AdminSidebarViewComponent : ViewComponent
{
    private readonly IAuthorizationService _authorization;
    private readonly IWebHostEnvironment _environment;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpContextAccessor _httpContext;

    public AdminSidebarViewComponent(
        IAuthorizationService authorization,
        IWebHostEnvironment environment,
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContext)
    {
        _authorization = authorization;
        _environment = environment;
        _serviceProvider = serviceProvider;
        _httpContext = httpContext;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var logger = _serviceProvider.GetService<ILogger<AdminSidebarViewComponent>>()
                     ?? NullLogger<AdminSidebarViewComponent>.Instance;

        var activeController = (string?)RouteData.Values["controller"];
        var activeAction = (string?)RouteData.Values["action"];
        var visibleGroups = new List<AdminSidebarGroupViewModel>(AdminNavTree.Groups.Count);

        foreach (var group in AdminNavTree.Groups)
        {
            var visibleItems = new List<AdminSidebarItemViewModel>(group.Items.Count);
            foreach (var item in group.Items)
            {
                if (item.EnvironmentGate is not null && !item.EnvironmentGate(_environment))
                    continue;

                if (item.Policy is not null)
                {
                    var auth = await _authorization.AuthorizeAsync(HttpContext.User, null, item.Policy);
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
                        pill = await item.PillCount(_serviceProvider);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to compute pill count for nav item {LabelKey}", item.LabelKey);
                        pill = null;
                    }
                }

                visibleItems.Add(new AdminSidebarItemViewModel(
                    LabelKey: item.LabelKey,
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
                visibleGroups.Add(new AdminSidebarGroupViewModel(group.LabelKey, visibleItems));
        }

        return View(new AdminSidebarViewModel(visibleGroups));
    }
}
