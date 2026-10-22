using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.CityPlanning;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("City Planning", [
            // The page also admits city-planning team members; they reach it from the member-side City page.
            new("Barrio map", "CityPlanning", "Admin", null, null, "fa-solid fa-map", PolicyNames.CampAdminOrAdmin)
        ])
    ];
}
