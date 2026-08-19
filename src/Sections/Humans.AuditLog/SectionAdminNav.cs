using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.AuditLog;

/// <summary>AuditLog's admin sidebar contribution — the "Audit" group (nobodies-collective/Humans#1077).</summary>
/// <remarks>
/// Audit is a Crosscut (memory/architecture/crosscut-purity.md), not Governance — Board usage
/// is audience, never ownership (memory/architecture/governance-scope.md).
/// </remarks>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Audit", [
            new("Audit log", "AuditLog", "Index", null, null, "fa-solid fa-book-open", PolicyNames.BoardOrAdmin)
        ], Weight: 80)
    ];
}
