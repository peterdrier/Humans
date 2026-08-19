using Humans.Base.Interfaces;

namespace Humans.Governance;

/// <summary>Governance's member-dashboard content: the Colaborador/Asociado term card.</summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, "MemberTermStatus", Weight: 10)];
}
