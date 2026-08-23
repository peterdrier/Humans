using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Settings;

/// <summary>
/// Settings' admin nav group — the app-wide event values (#1104).
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Settings", System: true, Items: [
            new("Event settings", "SettingsAdmin", "Index", null, null, "fa-solid fa-calendar-days", PolicyNames.AdminOnly, Weight: 0),
            // Retires with the carry screen, once the values are across.
            new("Carry event settings", "EventSettingsCarryAdmin", "Index", null, null, "fa-solid fa-arrow-right-arrow-left", PolicyNames.AdminOnly, Weight: 10)
        ], Weight: 100)
    ];
}
