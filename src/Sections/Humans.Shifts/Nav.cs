using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.Shifts;

/// <summary>Member top-nav contribution — was the second-to-last link in Shell's <c>_Layout.cshtml</c>.</summary>
public sealed class Nav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Shifts", Controller: "Shifts", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 60)];
}
