using Humans.Settings.Contracts;
using Humans.Settings.ViewComponents;

namespace Humans.Settings;

/// <summary>
/// Settings' /Settings tab — the app-wide event values (peterdrier/Humans#1628).
/// </summary>
/// <remarks>
/// <see cref="SettingsTab.Policy"/> is deliberately null: /Settings must never 404 for a
/// plain authenticated member, so the tab is offered to everyone and decides for itself
/// (in <c>EventSettingsTabViewComponent</c>) whether the viewer gets the editable form or
/// a read-only view. The label is a SharedResource key (<c>Settings_TabEvent</c>) —
/// <c>SettingsTabsViewComponent</c> renders it and cannot see this section's own private
/// resource set, even though both now live in Settings: a tab's own contributor is not
/// always Settings itself.
/// </remarks>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("event", "Settings_TabEvent", typeof(EventSettingsTabViewComponent))
    ];
}
