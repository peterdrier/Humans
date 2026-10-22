using NodaTime;

namespace Humans.Tickets.Domain;

/// <summary>The next ticket sync reconciles the local attendee rows after a manual decision.</summary>
internal sealed class TicketTransferRequest
{
    public Guid Id { get; init; }

    public Guid OriginalTicketAttendeeId { get; init; }

    /// <summary>Not every query path includes it; consumers fall back to a repository lookup.</summary>
    public TicketAttendee? OriginalTicketAttendee { get; set; }

    public Guid SenderUserId { get; init; }

    public Guid ReceiverUserId { get; init; }

    /// <summary>
    /// Snapshot at request time, so a profile rename before processing doesn't change what the team reissues under.
    /// </summary>
    public string ReceiverLegalName { get; init; } = string.Empty;

    /// <summary>Snapshot at request time: where the ticket is reissued and the decision email goes.</summary>
    public string ReceiverEmail { get; init; } = string.Empty;

    public string SenderReason { get; init; } = string.Empty;

    public TicketTransferStatus Status { get; set; } = TicketTransferStatus.Pending;

    /// <summary>NotAttempted for manual ("mark successful") transfers.</summary>
    public TicketTransferVendorResult VendorResult { get; set; } = TicketTransferVendorResult.NotAttempted;

    /// <summary>The hold id on success, the failure detail otherwise.</summary>
    public string? VendorMessage { get; set; }

    public string? NewVendorTicketId { get; set; }

    /// <summary>
    /// Kept when a void-to-hold succeeded but the reissue failed, so an admin can retry from the held seat.
    /// </summary>
    public string? VendorHoldId { get; set; }

    // Dormant: unread, dropped after prod soak.
    public string VendorStepsJson { get; set; } = "[]";

    /// <summary>Null while Pending or when the Sender cancelled.</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>The required cancellation reason or an optional success note.</summary>
    public string? AdminNotes { get; set; }

    public Instant RequestedAt { get; init; }
    public Instant? DecidedAt { get; set; }
}
