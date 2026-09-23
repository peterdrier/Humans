using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Camps.ViewComponents;

namespace Humans.Camps;

/// <summary>
/// Camps' /Settings tab (peterdrier/Humans#1634): open seasons, moved off
/// <c>/Camps/Admin</c> — the rest of that page's Season Management card (Name Lock Date)
/// and its other content stay there (see <c>CampAdminController</c>).
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("barrios", "Settings_TabBarrios", typeof(CampBarriosSettingsTabViewComponent), PolicyNames.CampAdminOrAdmin)
    ];
}
