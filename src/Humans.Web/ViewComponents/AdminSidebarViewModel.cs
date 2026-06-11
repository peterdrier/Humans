namespace Humans.Web.ViewComponents;

public sealed record AdminSidebarViewModel(IReadOnlyList<AdminSidebarGroupViewModel> Groups);

public sealed record AdminSidebarGroupViewModel(string Label, IReadOnlyList<AdminSidebarItemViewModel> Items, bool System = false)
{
    public bool ContainsActive => Items.Any(i => i.IsActive);

    /// <summary>Sum of item pill counts, surfaced on the group header/chip while the items are hidden.</summary>
    public int? PillSum
    {
        get
        {
            var sum = Items.Sum(i => i.PillCount ?? 0);
            return sum > 0 ? sum : null;
        }
    }
}

public sealed record AdminSidebarItemViewModel(
    string Label,
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,
    string IconCssClass,
    bool IsActive,
    int? PillCount);
