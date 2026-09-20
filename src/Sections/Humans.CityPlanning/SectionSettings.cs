using Humans.Base.Authorization;
using Humans.Settings.Contracts;

namespace Humans.CityPlanning;

/// <summary>
/// City Planning's /Settings tab (peterdrier/Humans#1634): placement windows, scheduled
/// times, and registration info, moved off <c>/CityPlanning/BarrioMap/Admin</c>. GeoJSON
/// uploads, container admin, and export/import stay on that page — see
/// <c>CityPlanningController</c>.
/// </summary>
/// <remarks>
/// <see cref="PolicyNames.CampAdminOrAdmin"/> — the same narrower gate
/// <see cref="SectionAdminNav"/> already uses for the "Barrio map" nav item, not the
/// page's own wider self-gate (city-planning team members too). A city-planning team
/// member without CampAdmin reaches the page itself via the member-side City page, same
/// as before; this tab is narrower by the same precedent.
/// </remarks>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("city-planning", "Settings_TabCityPlanning", "CityPlanningSettingsTab", PolicyNames.CampAdminOrAdmin)
    ];
}
