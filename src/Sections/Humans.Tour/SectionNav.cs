using Humans.Base.Interfaces;

namespace Humans.Tour;

/// <summary>Signed out only: the signed-in nav has no room, so members get the dashboard card instead.</summary>
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
