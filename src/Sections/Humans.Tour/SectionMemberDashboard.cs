using Humans.Base.Interfaces;
using Humans.Tour.ViewComponents;

namespace Humans.Tour;

/// <summary>The signed-in entry point to /Tour: the nav link is offered only while signed out.</summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(TourCardViewComponent), Weight: 100)
    ];
}
