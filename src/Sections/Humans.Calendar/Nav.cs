using Humans.Base.Interfaces;

namespace Humans.Calendar;

/// <summary>
/// Member top-nav contribution — was the third link in Shell's <c>_Layout.cshtml</c>. "Calendar"
/// was a literal string, not a resource key, so it stays literal (a key with no entry renders
/// as itself).
/// </summary>
public sealed class Nav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Calendar", Controller: "Calendar", Action: "Index", Weight: 30)];
}
