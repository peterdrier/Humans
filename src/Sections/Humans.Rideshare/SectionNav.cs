using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Rideshare;

/// <summary>Member top-nav link to the rideshare board; label key lives in SharedResource.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new("Nav_Rideshare", Controller: "Rideshare", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 55)
    ];
}
