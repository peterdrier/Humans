namespace Humans.Tickets.Contracts;

/// <summary>Tickets' grantable role. Declared to Auth by Tickets' <c>Section</c>.</summary>
public static class TicketsRoles
{
    /// <summary>
    /// Tickets Admin: manages the ticket vendor integration, triggers syncs, generates discount codes and exports
    /// ticket data. Stored as <c>TicketAdmin</c> before 2026-10.
    /// </summary>
    public const string TicketsAdmin = "TicketsAdmin";
}
