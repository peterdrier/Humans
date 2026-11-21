using Humans.AuditLog.Contracts;

namespace Humans.Tickets;

/// <summary>The audit actions Tickets writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class TicketsAuditActions : AuditActionContributor
{
    // Retired with the transfer-request pages: nothing writes them; declared so stored 2026 rows keep their verb.
    public static readonly AuditActionDefinition TicketTransferRequested = new("TicketTransferRequested");

    public static readonly AuditActionDefinition TicketTransferApproved = new("TicketTransferApproved");

    public static readonly AuditActionDefinition TicketTransferRejected = new("TicketTransferRejected");

    public static readonly AuditActionDefinition TicketTransferCancelled = new("TicketTransferCancelled");

    public static readonly AuditActionDefinition TicketTransferAutoFailed = new("TicketTransferAutoFailed");

    public static readonly AuditActionDefinition TicketContactsImported = new("TicketContactsImported");

    // Buyer names and amounts, for the accountant.
    public static readonly AuditActionDefinition TicketDonationsExported = new("TicketDonationsExported");

    public static readonly AuditActionDefinition GateTerminalPasswordSet = new("GateTerminalPasswordSet");

    public static readonly AuditActionDefinition TicketAttendeesExported = new("TicketAttendeesExported");

    public static readonly AuditActionDefinition TicketOrdersExported = new("TicketOrdersExported");

    public static readonly AuditActionDefinition ParticipationBackfilled = new("ParticipationBackfilled");

    public static readonly AuditActionDefinition TicketSalesEventSaved = new("TicketSalesEventSaved");

    public static readonly AuditActionDefinition TicketResaleOpenSet = new("TicketResaleOpenSet");

    public static readonly AuditActionDefinition TicketCheckoutPaid = new("TicketCheckoutPaid");

    /// <summary>Failed, expired or cancelled, as the backend reported it.</summary>
    public static readonly AuditActionDefinition TicketCheckoutClosed = new("TicketCheckoutClosed");

    public static readonly AuditActionDefinition TicketCheckoutRefunded = new("TicketCheckoutRefunded");

    /// <summary>Automation left the ticketing team something to do: someone who ended up with two tickets, a refund to pay by hand.</summary>
    public static readonly AuditActionDefinition TicketCheckoutNeedsAttention = new("TicketCheckoutNeedsAttention");

    public static readonly AuditActionDefinition TicketResaleOffered = new("TicketResaleOffered");

    public static readonly AuditActionDefinition TicketResaleWithdrawn = new("TicketResaleWithdrawn");

    public static readonly AuditActionDefinition TicketCheckoutTicketRenamed = new("TicketCheckoutTicketRenamed");

    public static readonly AuditActionDefinition TicketCheckoutTicketVoided = new("TicketCheckoutTicketVoided");

    public static readonly AuditActionDefinition TicketCompanionIssued = new("TicketCompanionIssued");

    /// <summary>Someone holding Tickets Override stepped past a ticket rule by hand.</summary>
    public static readonly AuditActionDefinition TicketOverride = new("TicketOverride");

    /// <summary>A super admin cancelled an event; the reconcile job then voids and refunds its orders.</summary>
    public static readonly AuditActionDefinition TicketSalesEventCancelled = new("TicketSalesEventCancelled");

    /// <summary>The ticketing team took a holder's ticket out of the resale queue.</summary>
    public static readonly AuditActionDefinition TicketResaleWithdrawnByTeam = new("TicketResaleWithdrawnByTeam");

    /// <summary>The ticketing team approved or declined a holder's companion request.</summary>
    public static readonly AuditActionDefinition TicketCompanionRequestDecided = new("TicketCompanionRequestDecided");
    /// <summary>Ticket emails and the receipt, queued automatically once an order's tickets were issued.</summary>
    public static readonly AuditActionDefinition TicketDeliverySent = new("TicketDeliverySent");

    public static readonly AuditActionDefinition TicketDeliveryResent = new("TicketDeliveryResent");

    public static readonly AuditActionDefinition TicketJoinInvitationSent = new("TicketJoinInvitationSent");

    public static readonly AuditActionDefinition TicketScheduledSyncEnabled = new("TicketScheduledSyncEnabled");

    public static readonly AuditActionDefinition TicketScheduledSyncDisabled = new("TicketScheduledSyncDisabled");
}
