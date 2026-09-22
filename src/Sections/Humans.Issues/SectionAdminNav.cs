using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Issues;

/// <summary>Issues' admin sidebar group, which Feedback's legacy queue also joins.</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Issues", [
            new("Issues", "Issues", "Index", null, null, "fa-solid fa-bug", PolicyNames.AdminOnly, Weight: 0)
        ])
    ];
}
