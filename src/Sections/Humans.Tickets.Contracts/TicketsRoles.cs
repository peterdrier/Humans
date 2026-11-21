namespace Humans.Tickets.Contracts;

/// <summary>Tickets' grantable role. Declared to Auth by Tickets' <c>Section</c>.</summary>
public static class TicketsRoles
{
    /// <summary>
    /// Tickets Admin: manages the ticket vendor integration, triggers syncs, generates discount codes and exports
    /// ticket data. Stored as <c>TicketAdmin</c> before 2026-10.
    /// </summary>
    public const string TicketsAdmin = "TicketsAdmin";

    /// <summary>
    /// Tickets Override: the one or two people who may step past the ticket rules by hand (restore a voided ticket, move
    /// a reduced price ticket, a companion past capacity). Every override is audited.
    /// </summary>
    public const string TicketsOverride = "TicketsOverride";
}
