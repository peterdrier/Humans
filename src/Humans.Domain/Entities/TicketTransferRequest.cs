using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A user-initiated request to transfer a TicketAttendee (issued ticket) from
/// the Sender (current ticket holder, must be the order's MatchedUserId) to
/// a target Humans user (the Receiver). Pending until a TicketAdmin decides;
/// the Sender may also cancel while still Pending. The ticket team processes
/// the actual void+reissue manually in the TicketTailor dashboard, then marks
/// the request Approved ("transfer successful") or Rejected ("cancel with a
/// reason"). The next ticket sync reconciles the local attendee rows — this
/// app never calls the vendor for a transfer.
/// </summary>
public class TicketTransferRequest
{
    public Guid Id { get; init; }

    /// <summary>FK to the TicketAttendee being transferred (the original issued ticket).</summary>
    public Guid OriginalTicketAttendeeId { get; init; }

    /// <summary>Navigation to the original attendee row. Nullable because not all
    /// query paths Include() it; consumers fall back to a direct repo lookup.</summary>
    public TicketAttendee? OriginalTicketAttendee { get; set; }

    /// <summary>Humans user sending the ticket (the buyer / current holder).</summary>
    public Guid SenderUserId { get; init; }

    /// <summary>Target Humans user (Receiver).</summary>
    public Guid ReceiverUserId { get; init; }

    /// <summary>
    /// Snapshot of the Receiver's legal name (Profile.FullName) at request
    /// time, so a profile rename between request and processing doesn't change
    /// what the ticket team reissues under. Shown to the team + in emails.
    /// </summary>
    public string ReceiverLegalName { get; init; } = string.Empty;

    /// <summary>
    /// Snapshot of the Receiver's primary email at request time — the address
    /// the team reissues the ticket to, and where the decision email is sent.
    /// </summary>
    public string ReceiverEmail { get; init; } = string.Empty;

    /// <summary>Free-text reason from the Sender (visible to admin).</summary>
    public string SenderReason { get; init; } = string.Empty;

    /// <summary>Lifecycle state. See <see cref="TicketTransferStatus"/>.</summary>
    public TicketTransferStatus Status { get; set; } = TicketTransferStatus.Pending;

    // ── Dormant vendor-writeback columns ────────────────────────────────────────
    // The automated TicketTailor void+reissue engine was removed when transfers
    // moved to manual processing. These columns are no longer read or written by
    // any code; they linger until a follow-up PR drops them after prod soak
    // (memory/architecture/no-drops-until-prod-verified.md). Do not add new readers.

    public TicketTransferVendorResult VendorResult { get; set; } = TicketTransferVendorResult.NotAttempted;
    public string? VendorMessage { get; set; }
    public string? NewVendorTicketId { get; set; }
    public string VendorStepsJson { get; set; } = "[]";

    /// <summary>TicketAdmin who decided (null while Pending or if Cancelled by the Sender).</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>Free-text from the deciding admin (required cancellation reason or optional success note).</summary>
    public string? AdminNotes { get; set; }

    public Instant RequestedAt { get; init; }
    public Instant? DecidedAt { get; set; }
}
