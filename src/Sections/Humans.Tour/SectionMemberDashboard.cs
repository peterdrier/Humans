using Humans.Base.Interfaces;

namespace Humans.Tour;

/// <summary>
/// The member dashboard's Tour card: the signed-in entry point, since the nav link is offered
/// only while signed out. Contributed here rather than named by string in Shell's Dashboard,
/// so the section-activation scan sees it. The weight sorts it last in the slot.
/// </summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, "TourCard", Weight: 100)
    ];
}
