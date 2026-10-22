using Humans.Base.Interfaces;

namespace Humans.Teams;

internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Teams", Controller: "Team", Action: "Index", Weight: 20)];
}
