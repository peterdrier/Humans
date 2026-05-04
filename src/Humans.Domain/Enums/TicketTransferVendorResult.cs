namespace Humans.Domain.Enums;

/// <summary>
/// Outcome of the TicketTailor writeback when a transfer is approved.
/// <list type="bullet">
///   <item><see cref="NotAttempted"/> — only set on Pending/Rejected/Cancelled rows.</item>
///   <item><see cref="Succeeded"/> — original ticket voided AND replacement issued.</item>
///   <item><see cref="VoidSucceededIssueFailed"/> — recoverable; admin can retry just the issue half.</item>
///   <item><see cref="Failed"/> — neither leg succeeded; transfer is Option-C-only (admin must edit TT dashboard).</item>
/// </list>
/// </summary>
public enum TicketTransferVendorResult
{
    NotAttempted,
    Succeeded,
    VoidSucceededIssueFailed,
    Failed,
}
