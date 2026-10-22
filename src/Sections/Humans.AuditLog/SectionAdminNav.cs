using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.AuditLog;

/// <summary>Its own group, not Governance's: Audit is a crosscut, and Board use is audience, not ownership.</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Audit", [
            new("Audit log", "AuditLog", "Index", null, null, "fa-solid fa-book-open", PolicyNames.BoardOrAdmin)
        ])
    ];
}
