using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Tickets;

public interface ITicketTransferService
{
    /// <summary>
    /// Resolve recipient candidates for a free-text query. Email queries
    /// (containing '@') are exact, case-insensitive against verified UserEmails
    /// and return at most one candidate so we don't fuzzy-leak addresses.
    /// Non-email queries match burner names (case-insensitive contains) and
    /// return up to 10 candidates ordered by display name — the caller renders
    /// the list and lets the user pick.
    /// </summary>
    Task<IReadOnlyList<RecipientLookupResultDto>> LookupRecipientsAsync(
        string query, Guid requesterUserId, CancellationToken ct = default);

    /// <summary>
    /// Build the recipient card for a specific UserId — used after the user
    /// picks one entry from a multi-match burner-name search. Returns null
    /// if the user is the requester themselves or doesn't exist.
    /// </summary>
    Task<RecipientLookupResultDto?> GetRecipientCardAsync(
        Guid recipientUserId, Guid requesterUserId, CancellationToken ct = default);

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

    /// <summary>
    /// Read-side composition for the admin Detail review screen: the row plus
    /// profile cards for requester and recipient. Returns null if no transfer
    /// exists with that id.
    /// </summary>
    Task<TicketTransferDetailDto?> GetDetailAsync(
        Guid transferRequestId, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);
}
