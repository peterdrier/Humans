using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.EarlyEntry;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Early Entry", [
            new("Early entry", "EarlyEntryRoster", "Index", null, null, "fa-solid fa-door-open", PolicyNames.ShiftDashboardAccess)
        ])
    ];
}
