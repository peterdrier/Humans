using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Camps;

/// <summary>Camps' contribution to the shared "Barrios" admin group (nobodies-collective/Humans#1077).</summary>
public sealed class AdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Barrios", [
            new("Overview",   "CampAdmin",      "Index",      null, null, "fa-solid fa-tents",           PolicyNames.CampAdminOrAdmin, Weight: 0),
            new("Roles",      "CampAdmin",      "Roles",      null, null, "fa-solid fa-user-tag",        PolicyNames.CampAdminOrAdmin, Weight: 10),
            new("Compliance", "CampCompliance", "Compliance", null, null, "fa-solid fa-clipboard-check", PolicyNames.CampComplianceAccess, Weight: 20)
        ], Weight: 30)
    ];
}
