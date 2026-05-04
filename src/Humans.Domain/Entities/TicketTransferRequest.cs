using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A user-initiated request to transfer a TicketAttendee (issued ticket) from
/// the requester (current ticket holder, must be the order's MatchedUserId) to
/// a target Humans user. Pending until a TicketAdmin approves or rejects;
/// requester may also cancel while still Pending. Approved transfers fire a
/// TicketTailor void+reissue; if that fails, Option-C fallback applies (the
/// request still ends in Approved state but VendorResult records the failure
/// so an admin can edit the TT dashboard manually).
/// </summary>
public class TicketTransferRequest
{
    public Guid Id { get; init; }

    /// <summary>FK to the TicketAttendee being transferred (the original issued ticket).</summary>
    public Guid OriginalTicketAttendeeId { get; init; }

    /// <summary>Navigation to the original attendee row.</summary>
    public TicketAttendee OriginalTicketAttendee { get; set; } = null!;

    /// <summary>Humans user who initiated the transfer (the buyer / current holder).</summary>
    public Guid RequesterUserId { get; init; }

    /// <summary>Target Humans user (recipient).</summary>
    public Guid RecipientUserId { get; init; }

    /// <summary>
    /// Snapshot of the recipient's display name at request time, in case their
    /// profile is renamed between request and approval.
    /// </summary>
    public string RecipientDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Snapshot of the recipient's preferred email at request time. This is
    /// what gets sent to TT as the new attendee's email on reissue.
    /// </summary>
    public string RecipientEmail { get; init; } = string.Empty;

    /// <summary>Free-text reason from the requester (visible to admin).</summary>
    public string RequesterReason { get; init; } = string.Empty;

    /// <summary>Lifecycle state. See <see cref="TicketTransferStatus"/>.</summary>
    public TicketTransferStatus Status { get; set; } = TicketTransferStatus.Pending;

    /// <summary>Vendor writeback outcome. NotAttempted until status is Approved.</summary>
    public TicketTransferVendorResult VendorResult { get; set; } = TicketTransferVendorResult.NotAttempted;

    /// <summary>Optional message captured during the vendor call (error text on failure, hold id on success-with-hold).</summary>
    public string? VendorMessage { get; set; }

    /// <summary>
    /// New TT issued-ticket id, set when the void+reissue succeeded. Null
    /// otherwise. The fresh TicketAttendee row created at approval time will
    /// also carry this in <see cref="TicketAttendee.VendorTicketId"/>.
    /// </summary>
    public string? NewVendorTicketId { get; set; }

    /// <summary>TicketAdmin who decided (null while Pending or if Cancelled by requester).</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>Free-text from the deciding admin (rejection reason or approval note).</summary>
    public string? AdminNotes { get; set; }

    public Instant RequestedAt { get; init; }
    public Instant? DecidedAt { get; set; }
}
