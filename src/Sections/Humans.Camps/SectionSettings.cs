using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Camps.ViewComponents;

namespace Humans.Camps;

/// <summary>Open seasons only; Name Lock Date and the directory live on <c>/Camps/Admin</c>.</summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("barrios", "Settings_TabBarrios", typeof(CampBarriosSettingsTabViewComponent), PolicyNames.CampAdminOrAdmin)
    ];
}
