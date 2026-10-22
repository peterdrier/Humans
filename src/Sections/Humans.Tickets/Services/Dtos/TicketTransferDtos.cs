using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using NodaTime;

namespace Humans.Tickets.Services.Dtos;

internal sealed record TicketTransferRequestDto(
    Guid OriginalAttendeeId,
    Guid ReceiverUserId,
    string Reason);

internal sealed record TicketTransferRowDto(
    Guid Id,
    Guid OriginalAttendeeId,
    string OriginalAttendeeName,
    string TicketTypeName,
    TicketAttendeeStatus OriginalAttendeeStatus,
    Instant? OriginalAttendeeCheckedInAt,
    Guid SenderUserId,
    Guid ReceiverUserId,
    string ReceiverLegalName,
    string ReceiverEmail,
    string SenderReason,
    TicketTransferStatus Status,
    TicketTransferVendorResult VendorResult,
    string? VendorMessage,
    Guid? DecidedByUserId,
    string? AdminNotes,
    Instant RequestedAt,
    Instant? DecidedAt);

internal sealed record TicketTransferDetailDto(
    TicketTransferRowDto Row,
    string? OrderDashboardUrl,
    string OriginalAttendeeVendorTicketId,
    string? OriginalAttendeeEmail,
    string OrderVendorId,
    Instant OrderPurchasedAt,
    string OrderBuyerEmail,
    IReadOnlyList<string> SiblingVendorTicketIds);

/// <summary>Legal name is resolved server-side: the person-search API omits it.</summary>
internal sealed record TicketTransferConfirmDto(
    Guid AttendeeId,
    string AttendeeName,
    string VendorTicketId,
    Guid ReceiverUserId,
    string ReceiverLegalName,
    string ReceiverEmail);

internal sealed record MyAttendeeRowDto(
    UserTicketHoldingRow Ticket,
    bool CanSendTransfer);
