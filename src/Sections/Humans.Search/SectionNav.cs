using Humans.Base.Interfaces;

namespace Humans.Search;

/// <summary>
/// Gated on sign-in, not <see cref="Humans.Base.Authorization.PolicyNames.AppAccess"/>: search works before a member
/// has an active profile.
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
