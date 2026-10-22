using Humans.Settings.Contracts;
using Humans.Settings.ViewComponents;

namespace Humans.Settings;

/// <summary>
/// No policy on purpose: /Settings must never 404 for a plain member, so the tab decides per viewer whether to show
/// the form or a read-only view.
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("event", "Settings_TabEvent", typeof(EventSettingsTabViewComponent))
    ];
}
