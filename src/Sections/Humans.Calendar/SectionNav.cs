using Humans.Base.Interfaces;

namespace Humans.Calendar;

/// <summary>
/// Member top-nav contribution. "Calendar" is a literal string, not a resource key, and stays
/// one — a key with no entry renders as itself, so "localizing" it would be invisible.
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Calendar", Controller: "Calendar", Action: "Index", Weight: 30)];
}
