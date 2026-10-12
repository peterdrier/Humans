using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Camps.ViewComponents;

namespace Humans.Camps;

/// <summary>
/// Camps' /Settings tab (peterdrier/Humans#1634): open seasons. Name Lock Date and the
/// rest of the directory live on <c>/Camps/Admin</c> (see <c>CampAdminController</c>).
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("barrios", "Settings_TabBarrios", typeof(CampBarriosSettingsTabViewComponent), PolicyNames.CampAdminOrAdmin)
    ];
}
