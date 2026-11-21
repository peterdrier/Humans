namespace Humans.Tickets.Contracts;

/// <summary>Tickets' authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class TicketsPolicies
{
    /// <summary>Tickets Admin, Board or Admin: the ticket, transfer, contacts and gate admin pages.</summary>
    public const string Read = "TicketsRead";

    /// <summary>Tickets Admin or Admin: syncs, exports, transfer decisions, discount codes and gate settings.</summary>
    public const string Manage = "TicketsManage";

    /// <summary>Tickets Override or Admin: the audited manual overrides that step past the ticket rules.</summary>
    public const string Override = "TicketsOverride";
}
