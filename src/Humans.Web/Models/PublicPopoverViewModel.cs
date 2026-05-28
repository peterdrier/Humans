namespace Humans.Web.Models;

/// <summary>
/// Minimal model for the anonymous-viewer popover served on public team pages
/// (issue #771). Intentionally excludes city/country, full team list, languages,
/// tier badge, and suspended badge — only data that is already publicly visible
/// on the rendering team page is allowed.
/// </summary>
public sealed class PublicPopoverViewModel
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> RoleLabels { get; init; } = [];
}
