using Humans.Base.Interfaces;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts;

internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, typeof(DashboardShiftsViewComponent), Weight: 30)];
}
