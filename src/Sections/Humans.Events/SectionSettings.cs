using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Events.ViewComponents;

namespace Humans.Events;

internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("event-guide", "Settings_TabEventGuide", typeof(EventGuideSettingsTabViewComponent), PolicyNames.EventsAdminOrAdmin)
    ];
}
