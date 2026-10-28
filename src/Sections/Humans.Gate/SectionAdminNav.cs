using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Gate;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Gate", [
            new("Terminal",   "TicketsGateAdmin", "Index", null, null, "fa-solid fa-key",     PolicyNames.TicketAdminOrAdmin),
            new("Vendor check-in backfill", "GateVendorBackfillAdmin", "Index", null, null, "fa-solid fa-cloud-arrow-up", PolicyNames.AdminOnly)
        ])
    ];
}
