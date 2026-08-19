using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tickets;

/// <summary>
/// Tickets' admin sidebar contribution — the "Tickets" group, shared with Campaigns, Scanner,
/// Gate and EarlyEntry (nobodies-collective/Humans#1077). Weights preserve the pre-move
/// traffic-ordered tree exactly; do not re-sort.
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Tickets", [
            new("Tickets",           "Ticket",               "Index", null, null, "fa-solid fa-ticket",      PolicyNames.TicketAdminBoardOrAdmin, Weight: 0),
            new("Transfer requests", "TicketTransferAdmin",  "Index", null, null, "fa-solid fa-right-left",  PolicyNames.TicketAdminOrAdmin, Weight: 10,
                 PillCount: PillCounts.TransferQueue),
            new("Attendee contacts", "TicketsContactsAdmin", "Index", null, null, "fa-solid fa-address-book", PolicyNames.TicketAdminOrAdmin, Weight: 20),
            new("Onsite roster",     "TicketsOnsiteAdmin",   "Index", null, null, "fa-solid fa-clipboard-list", PolicyNames.ScannerAccess, Weight: 30)
        ], Weight: 0)
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> TransferQueue(IServiceProvider sp)
    {
        var transfers = sp.GetRequiredService<ITicketTransferQueue>();
        var count = await transfers.CountPendingAsync();
        return count > 0 ? count : null;
    }
}
