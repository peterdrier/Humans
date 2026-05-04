using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Tickets;

public interface ITicketTransferService
{
    /// <summary>
    /// Resolve a recipient by email (exact, case-insensitive against UserEmails)
    /// or burner-name wildcard. Returns null for zero or ambiguous matches —
    /// the caller is required to render exactly-one before allowing submission.
    /// </summary>
    Task<RecipientLookupResultDto?> LookupRecipientAsync(
        string query, Guid requesterUserId, CancellationToken ct = default);

    /// <summary>
    /// Create a Pending TicketTransferRequest. Validates: requester owns the
    /// attendee, attendee is Valid, no existing Pending request, recipient is
    /// not the requester, recipient does not already hold a Valid/CheckedIn
    /// ticket for the same event.
    /// </summary>
    Task<TicketTransferRowDto> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid requesterUserId, CancellationToken ct = default);

    /// <summary>
    /// Cancel a Pending request. Only the original requester may cancel.
    /// </summary>
    Task CancelAsync(Guid transferRequestId, Guid requesterUserId, CancellationToken ct = default);

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

    Task<IReadOnlyList<TicketTransferRowDto>> GetByRequesterAsync(
        Guid userId, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);
}
