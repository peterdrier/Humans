using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>
/// The admin sidebar row a ticketing platform section contributes: its own group, opening Tickets' status page for its
/// key. Tickets owns the page and the route; the platform owns its row and name.
/// </summary>
public static class TicketsPlatformAdminNav
{
    public static AdminNavGroup Group(string label, string platformKey, string iconCssClass) =>
        new(label, [new AdminNavItem(label, "TicketsPlatformAdmin", "Index", new { key = platformKey }, null,
            iconCssClass, TicketsPolicies.Read)]);
}
