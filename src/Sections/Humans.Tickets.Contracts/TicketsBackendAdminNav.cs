using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>
/// The admin sidebar row a checkout backend section contributes: its own group, opening Tickets' status page for its
/// key. Tickets owns the page and the route; the backend owns its row and name.
/// </summary>
public static class TicketsBackendAdminNav
{
    public static AdminNavGroup Group(string label, string backendKey, string iconCssClass) =>
        new(label, [new AdminNavItem(label, "TicketsBackendAdmin", "Index", new { key = backendKey }, null,
            iconCssClass, TicketsPolicies.Read)]);
}
