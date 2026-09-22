using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Settings;

/// <summary>
/// Settings' admin nav group — the app-wide event values
/// (nobodies-collective/Humans#1104).
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Settings", [
            // Points at the /Settings#event tab, not the retired /Settings/Admin screen
            // (peterdrier/Humans#1628).
            new("Event settings", "Settings", "Index", null, null, "fa-solid fa-calendar-days", PolicyNames.AdminOnly, Weight: 0)
        ])
    ];
}
