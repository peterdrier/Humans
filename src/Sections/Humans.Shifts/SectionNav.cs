using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.Shifts;

/// <summary>Member top-nav contribution.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Shifts", Controller: "Shifts", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 60)];
}
