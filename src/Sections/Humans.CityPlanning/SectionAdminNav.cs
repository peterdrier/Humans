using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.CityPlanning;

/// <summary>CityPlanning's contribution to the shared "Barrios" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Barrios", [
            // Page self-gates wider (city-planning team members too); they reach it
            // via the member-side City page, so the narrower nav policy is fine.
            new("Barrio map", "CityPlanning", "Admin", null, null, "fa-solid fa-map", PolicyNames.CampAdminOrAdmin, Weight: 30)
        ], Weight: 30)
    ];
}
