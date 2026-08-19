using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.CityPlanning;

/// <summary>Member top-nav contribution — was the fourth link in Shell's <c>_Layout.cshtml</c>.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_City", Controller: "CityPlanning", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 40)];
}
