using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.CityPlanning;

internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_City", Controller: "CityPlanning", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 40)];
}
