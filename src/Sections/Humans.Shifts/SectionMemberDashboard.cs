using Humans.Base.Interfaces;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts;

/// <summary>Shifts' member-dashboard content: confirmed signups, urgent shifts, and the
/// volunteer "Get involved" callout.</summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, typeof(DashboardShiftsViewComponent), Weight: 30)];
}
