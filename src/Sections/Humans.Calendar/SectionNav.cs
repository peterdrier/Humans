using Humans.Base.Interfaces;

namespace Humans.Calendar;

/// <summary>"Calendar" is a literal, not a resource key: a key with no entry renders as itself.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Calendar", Controller: "Calendar", Action: "Index", Weight: 30)];
}
