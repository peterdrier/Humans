using Humans.Base.Interfaces;

namespace Humans.Shifts;

/// <summary>Shifts' member-dashboard content: confirmed signups, urgent shifts, and the
/// volunteer "Get involved" callout.</summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, "DashboardShifts", Weight: 30)];
}
