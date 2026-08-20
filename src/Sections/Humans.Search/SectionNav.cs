using Humans.Base.Interfaces;

namespace Humans.Search;

/// <summary>
/// Member top-nav contribution — was the icon-only magnifying-glass link Shell's
/// <c>_Layout.cshtml</c> named by <c>asp-controller="Search"</c>
/// (nobodies-collective/Humans#1090). Gated on plain authentication, not
/// <see cref="Humans.Base.Authorization.PolicyNames.AppAccess"/> — search is available before
/// a human has an active profile, unlike most nav items.
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new(
            "Search",
            Controller: "Search",
            Action: "Index",
            Weight: 0,
            IconCssClass: "fa-solid fa-magnifying-glass",
            Visible: (_, user) => user.Identity?.IsAuthenticated == true)
    ];
}
