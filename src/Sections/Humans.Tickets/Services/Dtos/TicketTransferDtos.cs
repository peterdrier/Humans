using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using NodaTime;

namespace Humans.Tickets.Services.Dtos;

/// <summary>Submitted by the Sender when confirming the Receiver.</summary>
internal sealed record TicketTransferRequestDto(
    Guid OriginalAttendeeId,
    Guid ReceiverUserId,
    string Reason);

/// <summary>Admin decision payload.</summary>
internal sealed record TicketTransferDecisionDto(
    Guid TransferRequestId,
    bool Approve,
    string? AdminNotes);

/// <summary>Read-side DTO for the admin queue.</summary>
internal sealed record TicketTransferRowDto(
    Guid Id,
    Guid OriginalAttendeeId,
    string OriginalAttendeeName,
    string TicketTypeName,
    TicketAttendeeStatus OriginalAttendeeStatus,
    Instant? OriginalAttendeeCheckedInAt,
    Guid SenderUserId,
    string SenderDisplayName,
    Guid ReceiverUserId,
    string ReceiverLegalName,
    string ReceiverEmail,
    string SenderReason,
    TicketTransferStatus Status,
    TicketTransferVendorResult VendorResult,
    string? VendorMessage,
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
internal sealed record TicketTransferDetailDto(
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
internal sealed record TicketTransferConfirmDto(
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
internal sealed record MyAttendeeRowDto(
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
