using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Consent;

/// <summary>Consent's admin sidebar contribution — the "Consent" group.</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Consent", [
            new("Legal documents", "AdminLegalDocuments", "LegalDocuments", null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly)
        ])
    ];
}
