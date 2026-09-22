using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Gate;

/// <summary>
/// Gate's admin sidebar group: gate ops, then a temporary backfill (nobodies-collective/Humans#1077).
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Gate", [
            new("Terminal",   "TicketsGateAdmin", "Index", null, null, "fa-solid fa-key",     PolicyNames.TicketAdminOrAdmin),
            new("Staff PINs", "Gate",             "Admin", null, null, "fa-solid fa-sliders", PolicyNames.TicketAdminOrAdmin),
            new("Vendor check-in backfill", "GateVendorBackfillAdmin", "Index", null, null, "fa-solid fa-cloud-arrow-up", PolicyNames.AdminOnly)
        ])
    ];
}
