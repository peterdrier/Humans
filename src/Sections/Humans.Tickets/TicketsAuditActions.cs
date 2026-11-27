using Humans.AuditLog.Contracts;

namespace Humans.Tickets;

/// <summary>The audit actions Tickets writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class TicketsAuditActions : AuditActionContributor
{
    // Retired with the transfer-request pages: nothing writes them; declared so stored 2026 rows keep their verb.
    public static readonly AuditActionDefinition TicketTransferRequested = new("TicketTransferRequested", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketTransferApproved = new("TicketTransferApproved", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketTransferRejected = new("TicketTransferRejected", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketTransferCancelled = new("TicketTransferCancelled", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketTransferAutoFailed = new("TicketTransferAutoFailed", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketContactsImported = new("TicketContactsImported", AuditKept.Forever);

    // Buyer names and amounts, for the accountant.
    public static readonly AuditActionDefinition TicketDonationsExported = new("TicketDonationsExported", AuditKept.Forever);

    public static readonly AuditActionDefinition GateTerminalPasswordSet = new("GateTerminalPasswordSet", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketAttendeesExported = new("TicketAttendeesExported", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketOrdersExported = new("TicketOrdersExported", AuditKept.Forever);

    public static readonly AuditActionDefinition ParticipationBackfilled = new("ParticipationBackfilled", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketSalesEventSaved = new("TicketSalesEventSaved", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketResaleOpenSet = new("TicketResaleOpenSet", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketCheckoutPaid = new("TicketCheckoutPaid", AuditKept.Forever);

    /// <summary>Failed, expired or cancelled, as the platform reported it.</summary>
    public static readonly AuditActionDefinition TicketCheckoutClosed = new("TicketCheckoutClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketCheckoutRefunded = new("TicketCheckoutRefunded", AuditKept.Forever);

    /// <summary>Automation left the ticketing team something to do: someone who ended up with two tickets, a refund to pay by hand.</summary>
    public static readonly AuditActionDefinition TicketCheckoutNeedsAttention = new("TicketCheckoutNeedsAttention", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketResaleOffered = new("TicketResaleOffered", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketResaleWithdrawn = new("TicketResaleWithdrawn", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketCheckoutTicketRenamed = new("TicketCheckoutTicketRenamed", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketCheckoutTicketVoided = new("TicketCheckoutTicketVoided", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketCompanionIssued = new("TicketCompanionIssued", AuditKept.Forever);

    /// <summary>An Admin stepped past a ticket rule by hand.</summary>
    public static readonly AuditActionDefinition TicketOverride = new("TicketOverride", AuditKept.Forever);

    /// <summary>A super admin cancelled an event; the reconcile job then voids and refunds its orders.</summary>
    public static readonly AuditActionDefinition TicketSalesEventCancelled = new("TicketSalesEventCancelled", AuditKept.Forever);

    /// <summary>The ticketing team took a holder's ticket out of the resale queue.</summary>
    public static readonly AuditActionDefinition TicketResaleWithdrawnByTeam = new("TicketResaleWithdrawnByTeam", AuditKept.Forever);

    /// <summary>The ticketing team approved or declined a holder's companion request.</summary>
    public static readonly AuditActionDefinition TicketCompanionRequestDecided = new("TicketCompanionRequestDecided", AuditKept.Forever);
    /// <summary>Ticket emails and the receipt, queued automatically once an order's tickets were issued.</summary>
    public static readonly AuditActionDefinition TicketDeliverySent = new("TicketDeliverySent", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketDeliveryResent = new("TicketDeliveryResent", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketJoinInvitationSent = new("TicketJoinInvitationSent", AuditKept.Forever);

    /// <summary>An automatic mail to a buyer or holder: payment failed or lapsed, ticket resold or unsold, ticket renamed.</summary>
    public static readonly AuditActionDefinition TicketHolderNotified = new("TicketHolderNotified", AuditKept.Forever);

    /// <summary>The evening batch that refunds holders whose tickets sold in resale and whose buyers' payments held.</summary>
    public static readonly AuditActionDefinition TicketResaleRefundBatch = new("TicketResaleRefundBatch", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketScheduledSyncEnabled = new("TicketScheduledSyncEnabled", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketScheduledSyncDisabled = new("TicketScheduledSyncDisabled", AuditKept.Forever);

    public static readonly AuditActionDefinition TicketPostEventHoldSet = new("TicketPostEventHoldSet", AuditKept.Forever);

    /// <summary>The ticketing platform erased an event's personal data after its post-event hold.</summary>
    public static readonly AuditActionDefinition TicketEventPersonalDataErased = new("TicketEventPersonalDataErased", AuditKept.Forever);
}
