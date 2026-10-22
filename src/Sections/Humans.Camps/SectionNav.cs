namespace Humans.Camps;

internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Camp_Plural", Controller: "Camp", Action: "Index", Weight: 10)];
}
