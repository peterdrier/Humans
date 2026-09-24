using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Events.ViewComponents;

namespace Humans.Events;

/// <summary>
/// Events' /Settings tab (peterdrier/Humans#1634): submission window, publish time,
/// print slots — superseding the standalone <c>/Events/Admin/Settings</c> page.
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("event-guide", "Settings_TabEventGuide", typeof(EventGuideSettingsTabViewComponent), PolicyNames.EventsAdminOrAdmin)
    ];
}
