using Humans.Base.Interfaces;

namespace Humans.Web.ViewComponents;

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
