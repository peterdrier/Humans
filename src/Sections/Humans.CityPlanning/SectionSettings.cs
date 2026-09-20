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
/// <see cref="PolicyNames.CityPlanningMapAdmin"/> — CampAdmin/Admin *or* a city-planning
/// team member, the same audience <c>CityPlanningController.RequireMapAdminAsync</c>
/// admits and that these controls had on the page they moved off. The narrower
/// <see cref="PolicyNames.CampAdminOrAdmin"/> the "Barrio map" nav item uses would not do
/// here: that item is narrower only because the page stays reachable another way, while
/// this tab is now the only route to the controls.
/// </remarks>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("city-planning", "Settings_TabCityPlanning", "CityPlanningSettingsTab", PolicyNames.CityPlanningMapAdmin)
    ];
}
