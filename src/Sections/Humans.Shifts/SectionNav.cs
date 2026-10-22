using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.Shifts;

internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Shifts", Controller: "Shifts", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 60)];
}
