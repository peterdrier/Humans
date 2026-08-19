using Humans.Base.Interfaces;

namespace Humans.Teams;

/// <summary>Member top-nav contribution — was the second link in Shell's <c>_Layout.cshtml</c>.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Teams", Controller: "Team", Action: "Index", Weight: 20)];
}
