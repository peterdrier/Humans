using Humans.Base.Interfaces;

namespace Humans.Search;

/// <summary>
/// The icon-only magnifying-glass link in the member top-nav. Gated on plain
/// authentication, not <see cref="Humans.Base.Authorization.PolicyNames.AppAccess"/> —
/// search is available before a human has an active profile, unlike most nav items.
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
