using System.Security.Claims;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The items of one admin nav group the current user may see, with their pill counts — shared by
/// the sidebar (one row per group) and the tab strip (the current group's items).
/// </summary>
internal static class AdminNavItems
{
    public static async Task<List<AdminSidebarItemViewModel>> VisibleAsync(
        AdminNavGroup group,
        AdminNavItem? activeItem,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        IWebHostEnvironment environment,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        var visible = new List<AdminSidebarItemViewModel>(group.Items.Count);
        foreach (var item in group.Items)
        {
            if (item.EnvironmentGate is not null && !item.EnvironmentGate(environment))
                continue;

            if (item.Policy is not null)
            {
                var auth = await authorization.AuthorizeAsync(user, null, item.Policy);
                if (!auth.Succeeded) continue;
            }
            else if (item.RoleCheck is not null && !item.RoleCheck(user))
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
                }
            }

            visible.Add(new AdminSidebarItemViewModel(
                Label: item.Label,
                Controller: item.Controller,
                Action: item.Action,
                RouteValues: item.RouteValues,
                RawHref: item.RawHref,
                IconCssClass: item.IconCssClass,
                IsActive: ReferenceEquals(item, activeItem),
                PillCount: pill));
        }

        return visible;
    }
}
