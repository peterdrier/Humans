using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Gate;

/// <summary>
/// Gate's contribution to the shared "Tickets" (gate ops) and "Temp" admin groups
/// (nobodies-collective/Humans#1077).
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Tickets", [
            new("Gate terminal", "TicketsGateAdmin", "Index", null, null, "fa-solid fa-key",     PolicyNames.TicketAdminOrAdmin, Weight: 60),
            new("Gate settings", "Gate",             "Admin", null, null, "fa-solid fa-sliders", PolicyNames.TicketAdminOrAdmin, Weight: 70)
        ], Weight: 0),
        new("Temp", System: true, Items: [
            new("Vendor check-in backfill", "GateVendorBackfillAdmin", "Index", null, null, "fa-solid fa-cloud-arrow-up", PolicyNames.AdminOnly, Weight: 20)
        ], Weight: 170)
    ];
}
