namespace Humans.Web.ViewComponents;

public sealed record AdminSidebarViewModel(IReadOnlyList<AdminSidebarGroupViewModel> Groups);

public sealed record AdminSidebarGroupViewModel(string LabelKey, IReadOnlyList<AdminSidebarItemViewModel> Items);

public sealed record AdminSidebarItemViewModel(
    string LabelKey,
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,
    string IconCssClass,
    bool IsActive,
    int? PillCount);
