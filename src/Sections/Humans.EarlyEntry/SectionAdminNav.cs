using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.EarlyEntry;

/// <summary>EarlyEntry's contribution to the shared "Tickets" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Tickets", [
            // Early Entry aggregates providers from Shifts, Teams AND Camps; its
            // consumers are gate ops (scanner door context), so it lives here.
            new("Early entry", "EarlyEntryRoster", "Index", null, null, "fa-solid fa-door-open", PolicyNames.ShiftDashboardAccess, Weight: 80)
        ], Weight: 0)
    ];
}
