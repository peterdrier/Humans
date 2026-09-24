using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Base.ViewComponents;

/// <summary>Where the current admin page sits in the nav: its group, and the item (tab) it is or belongs under.</summary>
/// <param name="IsExact">The route is the item itself, not a subpage of it.</param>
public sealed record AdminNavLocation(AdminNavGroup Group, AdminNavItem Item, bool IsExact);

/// <summary>
/// The admin nav as rendered: every section's <see cref="ISectionAdminNav"/> contribution,
/// merged by group label and sorted alphabetically. No section link, policy gate or pill count
/// is hard-coded in Shell (nobodies-collective/Humans#1077).
/// </summary>
/// <remarks>
/// Items within a group order by weight; the sort is stable, so items that carry no weight keep
/// the order they were declared in.
/// </remarks>
public static class AdminNavComposition
{
    /// <summary>
    /// The <c>ViewData</c> key a subpage sets to name its parent item when that is not the first
    /// item on its controller: <c>"Action"</c> on the current controller, or <c>"Controller/Action"</c>.
    /// </summary>
    public const string ParentKey = "AdminNavParent";

    /// <summary>The Shell's dashboard controller: the sidebar's pinned "Dashboard" row, on no group.</summary>
    public const string DashboardController = "Admin";

    /// <summary>
    /// Whether a page renders in the admin shell: its endpoint is restricted by a policy beyond
    /// <see cref="PolicyNames.AppAccess"/>, and it is the dashboard or sits on a controller in the
    /// admin nav. A page members share (an imperative check, or none) keeps the member layout. It is
    /// the one rule every page follows (the root <c>_ViewStart</c>); whoever reached a restricted page
    /// passed its gate, so the sidebar and tabs then filter to what that user may see.
    /// </summary>
    /// <remarks>
    /// <c>_ViewStart</c> runs before the view names its <see cref="ParentKey"/>, so the controller
    /// match alone decides: a page whose controller has no nav item stays out of the shell.
    /// </remarks>
    public static bool IsAdminPage(
        IEnumerable<ISectionAdminNav> contributors, Endpoint? endpoint, string? controller, string? action)
    {
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || !endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a =>
                a.Roles is not null || (a.Policy is not null && !string.Equals(a.Policy, PolicyNames.AppAccess, StringComparison.Ordinal))))
            return false;

        return string.Equals(controller, DashboardController, StringComparison.OrdinalIgnoreCase)
            || Locate(Compose(contributors), controller, action, parent: null) is not null;
    }

    public static IReadOnlyList<AdminNavGroup> Compose(IEnumerable<ISectionAdminNav> contributors) =>
        [.. contributors
            .SelectMany(c => c.Groups())
            .GroupBy(g => g.Label, StringComparer.Ordinal)
            .Select(g => new AdminNavGroup(g.Key, [.. g.SelectMany(x => x.Items).OrderBy(i => i.Weight)]))
            .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Resolves the current route to its nav item: an exact controller+action match, else the
    /// <paramref name="parent"/> the page names, else the first item on the same controller.
    /// </summary>
    public static AdminNavLocation? Locate(
        IReadOnlyList<AdminNavGroup> groups, string? controller, string? action, string? parent)
    {
        if (string.IsNullOrEmpty(controller))
            return null;

        var exact = Find(groups, controller, action);
        if (exact is not null)
            return exact with { IsExact = true };

        if (!string.IsNullOrEmpty(parent))
        {
            var slash = parent.IndexOf('/', StringComparison.Ordinal);
            var named = slash < 0
                ? Find(groups, controller, parent)
                : Find(groups, parent[..slash], parent[(slash + 1)..]);
            if (named is not null)
                return named;
        }

        return Find(groups, controller, action: null);
    }

    private static AdminNavLocation? Find(IReadOnlyList<AdminNavGroup> groups, string controller, string? action)
    {
        foreach (var group in groups)
        {
            foreach (var item in group.Items)
            {
                if (string.Equals(item.Controller, controller, StringComparison.OrdinalIgnoreCase)
                    && (action is null || string.Equals(item.Action, action, StringComparison.OrdinalIgnoreCase)))
                    return new AdminNavLocation(group, item, IsExact: false);
            }
        }

        return null;
    }
}
