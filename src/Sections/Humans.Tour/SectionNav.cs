using Humans.Base.Interfaces;

namespace Humans.Tour;

/// <summary>
/// Anonymous-only top-nav link; signed-in members get Shell's dashboard tile instead.
/// Weight 1000 sorts it last among contributed links, beside Legal.
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
