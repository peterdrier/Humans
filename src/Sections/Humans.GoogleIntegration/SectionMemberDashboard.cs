using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration;

/// <summary>Dashboard contribution — was the member-dashboard invocation in Home/Dashboard.cshtml.</summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.MemberDashboard, "MyGoogleResources")];
}
