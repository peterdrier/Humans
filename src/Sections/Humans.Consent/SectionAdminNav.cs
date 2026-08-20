using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Consent;

/// <summary>Consent's admin sidebar contribution — the "Legal" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Legal", System: true, Items: [
            new("Legal documents", "AdminLegalDocuments", "LegalDocuments", null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly)
        ], Weight: 130)
    ];
}
