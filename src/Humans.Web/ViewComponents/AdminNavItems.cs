using System.Security.Claims;
using Humans.Base.Interfaces;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The items of one admin nav group the current user may see, with their pill counts — shared by
/// the sidebar (one row per group) and the tab strip (the current group's items).
/// </summary>
internal static class AdminNavItems
{
    private static readonly object RequestKey = new();

    /// <summary>
    /// The groups the user can see anything in, with the current route's group marked active —
    /// computed once per request: the sidebar and the tab strip both read it, so each item's policy
    /// check and pill count runs once per page.
    /// </summary>
    public static async Task<IReadOnlyList<AdminSidebarGroupViewModel>> ForRequestAsync(
        ViewComponent component,
        IEnumerable<ISectionAdminNav> navContributors,
        IAuthorizationService authorization,
        IWebHostEnvironment environment,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        var items = component.HttpContext.Items;
        if (items.TryGetValue(RequestKey, out var cached) && cached is IReadOnlyList<AdminSidebarGroupViewModel> known)
            return known;

        var groups = AdminNavComposition.Compose(navContributors);
        var location = AdminNavComposition.Locate(groups,
            (string?)component.RouteData.Values["controller"], (string?)component.RouteData.Values["action"],
            component.ViewData[AdminNavComposition.ParentKey] as string);

        var rows = new List<AdminSidebarGroupViewModel>(groups.Count);
        foreach (var group in groups)
        {
            var visible = await VisibleAsync(group, location?.Item, component.HttpContext.User,
                authorization, environment, serviceProvider, logger);
            if (visible.Count > 0)
                rows.Add(new AdminSidebarGroupViewModel(group.Label, visible, ReferenceEquals(group, location?.Group)));
        }

        items[RequestKey] = rows;
        return rows;
    }

    private static async Task<List<AdminSidebarItemViewModel>> VisibleAsync(
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
