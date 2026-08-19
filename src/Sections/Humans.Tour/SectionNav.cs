using Humans.Base.Interfaces;

namespace Humans.Tour;

/// <summary>
/// Member top-nav contribution — was the anonymous-only Tour link Shell's
/// <c>_Layout.cshtml</c> named by <c>asp-controller="Tour"</c>
/// (nobodies-collective/Humans#1090). Anonymous-only: the signed-in top nav is too busy for
/// a Tour slot — members get a dashboard tile instead (Home/Dashboard.cshtml). High weight so
/// it sorts last among contributed nav links, closest to its previous position beside Legal.
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
