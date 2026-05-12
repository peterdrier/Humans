using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A user-initiated request to transfer a TicketAttendee (issued ticket) from
/// the Sender (current ticket holder, must be the order's MatchedUserId) to
/// a target Humans user (the Receiver). Pending until a TicketAdmin approves
/// or rejects; the Sender may also cancel while still Pending. Approved
/// transfers fire a TicketTailor void+reissue; if that fails, Option-C
/// fallback applies (the request still ends in Approved state but VendorResult
/// records the failure so an admin can edit the TT dashboard manually).
/// </summary>
public class TicketTransferRequest
{
    public Guid Id { get; init; }

    /// <summary>FK to the TicketAttendee being transferred (the original issued ticket).</summary>
    public Guid OriginalTicketAttendeeId { get; init; }

    /// <summary>Navigation to the original attendee row.</summary>
    public TicketAttendee OriginalTicketAttendee { get; set; } = null!;

    /// <summary>Humans user sending the ticket (the buyer / current holder).</summary>
    public Guid SenderUserId { get; init; }

    /// <summary>Target Humans user (Receiver).</summary>
    public Guid ReceiverUserId { get; init; }

    /// <summary>
    /// Snapshot of the Receiver's legal name (Profile.FullName) at request
    /// time. Used for the vendor reissue payload so a profile rename between
    /// request and approval doesn't change what the vendor records.
    /// </summary>
    public string ReceiverLegalName { get; init; } = string.Empty;

    /// <summary>
    /// Snapshot of the Receiver's primary email at request time. This is what
    /// gets sent to TT as the new attendee's email on reissue.
    /// </summary>
    public string ReceiverEmail { get; init; } = string.Empty;

    /// <summary>Free-text reason from the Sender (visible to admin).</summary>
    public string SenderReason { get; init; } = string.Empty;

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

    /// <summary>TicketAdmin who decided (null while Pending or if Cancelled by the Sender).</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>Free-text from the deciding admin (rejection reason or approval note).</summary>
    public string? AdminNotes { get; set; }

    /// <summary>
    /// JSON-serialised list of <c>TicketTransferVendorStep</c> capturing each
    /// sub-step of the vendor writeback (void, issue, local upsert, retry,
    /// manual reconcile). Empty list <c>"[]"</c> for transfers created before
    /// this feature shipped; null is never expected.
    /// </summary>
    public string VendorStepsJson { get; set; } = "[]";

    public Instant RequestedAt { get; init; }
    public Instant? DecidedAt { get; set; }
}
