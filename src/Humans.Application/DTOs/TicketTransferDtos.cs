using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>Submitted by the Sender when confirming the Receiver.</summary>
public sealed record TicketTransferRequestDto(
    Guid OriginalAttendeeId,
    Guid ReceiverUserId,
    string Reason);

/// <summary>Admin decision payload.</summary>
public sealed record TicketTransferDecisionDto(
    Guid TransferRequestId,
    bool Approve,
    string? AdminNotes);

/// <summary>Read-side DTO for the admin queue.</summary>
public sealed record TicketTransferRowDto(
    Guid Id,
    Guid OriginalAttendeeId,
    string OriginalAttendeeName,
    string TicketTypeName,
    TicketAttendeeStatus OriginalAttendeeStatus,
    Guid SenderUserId,
    string SenderDisplayName,
    Guid ReceiverUserId,
    string ReceiverLegalName,
    string ReceiverEmail,
    string SenderReason,
    TicketTransferStatus Status,
    Guid? DecidedByUserId,
    string? DecidedByDisplayName,
    string? AdminNotes,
    Instant RequestedAt,
    Instant? DecidedAt);

/// <summary>
/// Read-side DTO for the admin Detail review screen — the row plus the ticket /
/// order context the team needs to process the transfer manually in the
/// TicketTailor dashboard.
/// </summary>
public sealed record TicketTransferDetailDto(
    TicketTransferRowDto Row,
    string? OrderDashboardUrl,
    string OriginalAttendeeVendorTicketId,
    string? OriginalAttendeeEmail,
    string OrderVendorId,
    Instant OrderPurchasedAt,
    string OrderBuyerEmail,
    IReadOnlyList<string> SiblingVendorTicketIds);

/// <summary>
/// Confirmation summary shown to the Sender before submitting the request:
/// which ticket, and the resolved Receiver legal name + email. Legal name is
/// resolved server-side because the person-search API deliberately omits it.
/// </summary>
public sealed record TicketTransferConfirmDto(
    Guid AttendeeId,
    string AttendeeName,
    string VendorTicketId,
    Guid ReceiverUserId,
    string ReceiverLegalName,
    string ReceiverEmail);

/// <summary>
/// One row in the "My tickets" attendee list, with eligibility flags for
/// sending and any pending outgoing transfer pre-computed in the service.
/// </summary>
public sealed record MyAttendeeRowDto(
    Guid AttendeeId,
    string AttendeeName,
    string? AttendeeEmail,
    string VendorTicketId,
    string TicketTypeName,
    TicketAttendeeStatus Status,
    bool IsCurrentOwner,
    bool CanSendTransfer,
    bool HasPendingOutgoingTransfer,
    Guid? PendingTransferRequestId);

/// <summary>
/// Render model for the <c>&lt;vc:ticket-stub&gt;</c> component — one held ticket
/// drawn as a physical admission stub. Shared by the transfer wizard,
/// <c>/Profile/Me</c>, and the homepage. A pending outgoing transfer renders a
/// "transfer pending" stamp on the stub.
/// </summary>
public sealed record TicketStubInfo(
    string AttendeeName,
    string? AttendeeEmail,
    string VendorTicketId,
    TicketAttendeeStatus Status,
    bool HasPendingTransfer,
    Guid? PendingTransferRequestId,
    LocalDate? EarlyEntryDate = null);
