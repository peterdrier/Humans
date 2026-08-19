using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Issues;

/// <summary>Issues' contribution to the shared "Feedback" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Feedback", [
            new("Issues", "Issues", "Index", null, null, "fa-solid fa-bug", PolicyNames.AdminOnly, Weight: 10)
        ], Weight: 90)
    ];
}
