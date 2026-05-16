using Humans.Application.DTOs;
using Humans.Domain.Enums;
using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Tickets;

[SurfaceBudget(12)]
public interface ITicketTransferService : IApplicationService
{
    /// <summary>
    /// Resolve Receiver candidates for a free-text query. Email queries
    /// (containing '@') are exact, case-insensitive against verified UserEmails
    /// and return at most one candidate so we don't fuzzy-leak addresses.
    /// Non-email queries match burner names (case-insensitive contains) and
    /// return up to 10 candidates ordered by display name — the caller renders
    /// the list and lets the user pick.
    /// </summary>
    Task<IReadOnlyList<ReceiverLookupResultDto>> LookupReceiversAsync(
        string query, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Build the Receiver card for a specific UserId — used after the user
    /// picks one entry from a multi-match burner-name search. Returns null
    /// if the user is the Sender themselves or doesn't exist.
    /// </summary>
    Task<ReceiverLookupResultDto?> GetReceiverCardAsync(
        Guid receiverUserId, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Build the homepage "My tickets" rows for a user, with send-eligibility
    /// flags pre-computed: only `Valid` attendees the user is the order's
    /// MatchedUserId for, with no existing Pending transfer, can be sent.
    /// </summary>
    Task<IReadOnlyList<MyAttendeeRowDto>> GetMyAttendeesAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Create a Pending TicketTransferRequest. Validates: Sender owns the
    /// attendee, attendee is Valid, Receiver is not the Sender. Receiver may
    /// already hold other tickets — that is allowed.
    /// </summary>
    Task<TicketTransferRowDto> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Cancel a Pending request. Only the original Sender may cancel.
    /// </summary>
    Task CancelAsync(Guid transferRequestId, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Approve a Pending request. Fires TT void+reissue; falls through to
    /// Option C (Approved + VendorResult.Failed) on vendor failure.
    /// </summary>
    Task<TicketTransferRowDto> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    /// <summary>
    /// Reject a Pending request. No TT call.
    /// </summary>
    Task<TicketTransferRowDto> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetBySenderAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Read-side composition for the admin Detail review screen: the row plus
    /// profile cards for Sender and Receiver. Returns null if no transfer
    /// exists with that id.
    /// </summary>
    Task<TicketTransferDetailDto?> GetDetailAsync(
        Guid transferRequestId, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Retry the issue half of a void+reissue that previously failed. Requires
    /// the request to be in state <c>Approved</c> with vendor result
    /// <c>VoidSucceededIssueFailed</c>; the hold id is read from the most
    /// recent successful <c>Void</c> step in <c>VendorStepsJson</c>. On
    /// success, inserts the new <c>TicketAttendee</c> row, sets
    /// <c>VendorResult=Succeeded</c>, appends a <c>RetryIssue</c> step, and
    /// audits + cache-invalidates. On failure, appends a failed
    /// <c>RetryIssue</c> step; state otherwise unchanged.
    /// </summary>
    Task<TicketTransferRowDto> RetryIssueAsync(
        Guid transferRequestId,
        Guid adminUserId,
        string? adminNotes,
        CancellationToken ct = default);
}
