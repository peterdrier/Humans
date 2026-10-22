using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Consent;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Consent", [
            new("Legal documents", "AdminLegalDocuments", "LegalDocuments", null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly)
        ])
    ];
}
