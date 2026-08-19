using Humans.Base.Interfaces;

namespace Humans.Camps;

/// <summary>Member top-nav contribution — was the first link in Shell's <c>_Layout.cshtml</c>.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Camp_Plural", Controller: "Camp", Action: "Index", Weight: 10)];
}
