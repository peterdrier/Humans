using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Services;

internal interface ITicketTransferService : ITicketTransferQueue, IApplicationService
{
    /// <summary>
    /// Only Valid, not-checked-in tickets with no Pending transfer can be sent. Voided holdings are dropped.
    /// </summary>
    Task<IReadOnlyList<MyAttendeeRowDto>> GetMyAttendeesAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Null unless the Sender owns a Valid attendee and the Receiver is a distinct resolvable user.
    /// </summary>
    Task<TicketTransferConfirmDto?> GetConfirmationAsync(
        Guid attendeeId, Guid receiverUserId, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Known rule failures return a resource-key refusal; dependency exceptions propagate.
    /// </summary>
    Task<TicketTransferMutationResult> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Only the original Sender may cancel. Known rule failures return a resource-key refusal; dependency exceptions
    /// propagate.
    /// </summary>
    Task<TicketTransferMutationResult> CancelAsync(Guid transferRequestId, Guid senderUserId, CancellationToken ct = default);

    /// <summary>
    /// "Transfer successful" without calling the vendor: the team already did the void and reissue by hand. The next
    /// ticket sync reconciles the local attendee rows.
    /// </summary>
    Task<TicketTransferMutationResult> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    /// <summary>
    /// On vendor failure the request stays Pending with the diagnostic recorded, so the team can finish by hand and
    /// fall back to <see cref="ApproveAsync"/>.
    /// </summary>
    [ExternalWrite]
    Task<TicketTransferMutationResult> ProcessTransferAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    /// <summary>
    /// Reissues from the hold a <see cref="TicketTransferVendorResult.VoidSucceededIssueFailed"/> recorded. An operator
    /// refusal when the request is not in that state; on another failure it stays Pending with the hold kept.
    /// </summary>
    [ExternalWrite]
    Task<TicketTransferMutationResult> RetryReissueAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    /// <summary>The reason is required and is emailed to the Sender and Receiver.</summary>
    Task<TicketTransferMutationResult> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetBySenderAsync(
        Guid userId, CancellationToken ct = default);

    Task<TicketTransferDetailDto?> GetDetailAsync(
        Guid transferRequestId, CancellationToken ct = default);
}

/// <summary>A transfer outcome; member refusals carry resource keys and operator refusals carry safe reasons.</summary>
internal sealed record TicketTransferMutationResult(TicketTransferRowDto? Transfer, string? RefusalKey, string? OperatorRefusal = null)
{
    public bool Succeeded => RefusalKey is null && OperatorRefusal is null;

    public static TicketTransferMutationResult Refused(string key) => new(null, key);
}
