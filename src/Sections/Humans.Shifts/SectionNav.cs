using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.Shifts;

/// <summary>Member top-nav contribution — was the second-to-last link in Shell's <c>_Layout.cshtml</c>.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Shifts", Controller: "Shifts", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 60)];
}
