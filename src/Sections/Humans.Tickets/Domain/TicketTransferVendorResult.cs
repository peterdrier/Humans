namespace Humans.Tickets.Domain;

/// <summary>
/// <see cref="VoidSucceededIssueFailed"/>: the vendor void committed but the reissue failed; retry from the held seat
/// or finish by hand. <see cref="Failed"/>: the void failed and nothing changed.
/// </summary>
internal enum TicketTransferVendorResult
{
    NotAttempted,
    Succeeded,
    VoidSucceededIssueFailed,
    Failed,
}
