using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Settings;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Settings", [
            new("Event settings", "Settings", "Index", null, null, "fa-solid fa-calendar-days", PolicyNames.AdminOnly, Weight: 0)
        ])
    ];
}
