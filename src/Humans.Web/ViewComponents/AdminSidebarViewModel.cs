namespace Humans.Web.ViewComponents;

/// <param name="ShowDashboard">The pinned Dashboard row: only for admin-shaped roles, the dashboard's own gate.</param>
public sealed record AdminSidebarViewModel(bool ShowDashboard, IReadOnlyList<AdminSidebarGroupViewModel> Groups);

/// <summary>One sidebar row: the group, linking to its first visible item.</summary>
public sealed record AdminSidebarGroupViewModel(string Label, IReadOnlyList<AdminSidebarItemViewModel> Items, bool IsActive)
{
    /// <summary>Where the row links, and whose icon it shows.</summary>
    public AdminSidebarItemViewModel First => Items[0];

    /// <summary>Sum of the group's item pill counts; the per-item pills show on its tab strip.</summary>
    public int? PillSum
    {
        get
        {
            var sum = Items.Sum(i => i.PillCount ?? 0);
            return sum > 0 ? sum : null;
        }
    }
}

/// <summary>One item of a group, rendered as a tab on the group's pages.</summary>
public sealed record AdminSidebarItemViewModel(
    string Label,
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,
    string IconCssClass,
    bool IsActive,
    int? PillCount);
