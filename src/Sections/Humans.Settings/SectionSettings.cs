using Humans.Base.Interfaces;

namespace Humans.Settings;

/// <summary>
/// Settings' /Settings tab — the app-wide event values (peterdrier/Humans#1628).
/// </summary>
/// <remarks>
/// <see cref="SettingsTab.Policy"/> is deliberately null: /Settings must never 404 for a
/// plain authenticated member, so the tab is offered to everyone and decides for itself
/// (in <c>EventSettingsTabViewComponent</c>) whether the viewer gets the editable form or
/// a read-only view.
/// </remarks>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("event", "Event", "EventSettingsTab")
    ];
}
