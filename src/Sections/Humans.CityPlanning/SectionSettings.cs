using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.CityPlanning.ViewComponents;

namespace Humans.CityPlanning;

/// <summary>
/// GeoJSON uploads, container admin and export/import stay on <c>/CityPlanning/BarrioMap/Admin</c>.
/// </summary>
/// <remarks>
/// Gated on <see cref="PolicyNames.CityPlanningMapAdmin"/>, not the narrower <see cref="PolicyNames.CampAdminOrAdmin"/>
/// the "Barrio map" nav item uses: that item can be narrower because the page stays reachable another way, while this
/// tab is the only route to its controls.
/// </remarks>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("city-planning", "Settings_TabCityPlanning", typeof(CityPlanningSettingsTabViewComponent), PolicyNames.CityPlanningMapAdmin)
    ];
}
