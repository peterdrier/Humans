using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Tickets;

public interface ITicketTransferService : IApplicationService
{
    /// <summary>
    /// Build the "My tickets" rows for a user, with send-eligibility flags
    /// pre-computed: only `Valid` attendees the user is the order's
    /// MatchedUserId for, with no existing Pending transfer, can be sent.
    /// </summary>
    Task<IReadOnlyList<MyAttendeeRowDto>> GetMyAttendeesAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Resolve the confirmation summary for a chosen (attendee, receiver) pair:
    /// validates the Sender owns a Valid attendee and the Receiver is a distinct
    /// resolvable user, and returns the ticket label + Receiver legal name and
    /// primary email for the confirm step. Returns null if the pair is invalid.
    /// </summary>
    Task<TicketTransferConfirmDto?> GetConfirmationAsync(
        Guid attendeeId, Guid receiverUserId, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Create a Pending TicketTransferRequest and email the Sender + the ticket
    /// team. Validates: Sender owns the attendee, attendee is Valid, Receiver is
    /// not the Sender, no existing Pending transfer for the attendee.
    /// </summary>
    Task<TicketTransferRowDto> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Cancel a Pending request. Only the original Sender may cancel.
    /// </summary>
    Task CancelAsync(Guid transferRequestId, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Mark a Pending request transferred ("transfer successful"). The ticket
    /// team has already done the void+reissue manually in TicketTailor, so this
    /// only records the decision, audits, and emails the Sender + Receiver. The
    /// next ticket sync reconciles the local attendee rows.
    /// </summary>
    Task<TicketTransferRowDto> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    /// <summary>
    /// Cancel a Pending request with a required reason. Records the decision,
    /// audits, and emails the Sender + Receiver with the reason.
    /// </summary>
    Task<TicketTransferRowDto> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetBySenderAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Read-side composition for the admin Detail review screen. Returns null if
    /// no transfer exists with that id.
    /// </summary>
    Task<TicketTransferDetailDto?> GetDetailAsync(
        Guid transferRequestId, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);
}
