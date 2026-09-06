using Humans.Base.Interfaces;

namespace Humans.Tour;

/// <summary>
/// The top-nav Tour link, offered only while signed out: the signed-in nav is too busy for a
/// Tour slot, so members get a dashboard card instead (Shell's Home/Dashboard.cshtml). The
/// weight sorts it last among contributed links, beside Legal.
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new(
            "Tour",
            Controller: "Tour",
            Action: "Index",
            Weight: 1000,
            Visible: (_, user) => user.Identity?.IsAuthenticated != true)
    ];
}
