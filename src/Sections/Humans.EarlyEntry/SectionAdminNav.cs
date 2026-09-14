using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.EarlyEntry;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Tickets", [
            // Under Tickets, not Shifts: gate ops is who looks for it. Moving it is a nav change, not a cleanup.
            new("Early entry", "EarlyEntryRoster", "Index", null, null, "fa-solid fa-door-open", PolicyNames.ShiftDashboardAccess, Weight: 80)
        ], Weight: 0)
    ];
}
