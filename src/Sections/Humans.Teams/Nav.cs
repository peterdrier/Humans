using Humans.Base.Interfaces;

namespace Humans.Teams;

/// <summary>Member top-nav contribution — was the second link in Shell's <c>_Layout.cshtml</c>.</summary>
public sealed class Nav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Teams", Controller: "Team", Action: "Index", Weight: 20)];
}
