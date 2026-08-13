namespace Humans.Tickets.Domain;

/// <summary>
/// Outcome of the automated TicketTailor void+reissue on a
/// <see cref="TicketTransferRequest"/>. Written by
/// <c>TicketTransferService.ProcessTransferAsync</c> and surfaced to the ticket team:
/// <see cref="Succeeded"/> → transfer done; <see cref="VoidSucceededIssueFailed"/> → void
/// committed at the vendor but reissue failed (finish manually against the recorded hold);
/// <see cref="Failed"/> → vendor void failed, nothing changed (process manually).
/// <see cref="NotAttempted"/> for manual ("mark successful") transfers.
/// </summary>
internal enum TicketTransferVendorResult
{
    NotAttempted,
    Succeeded,
    VoidSucceededIssueFailed,
    Failed,
}
