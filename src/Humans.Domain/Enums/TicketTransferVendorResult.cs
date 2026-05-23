namespace Humans.Domain.Enums;

/// <summary>
/// Vendor-writeback outcome retained ONLY as a dormant storage column on
/// <see cref="Humans.Domain.Entities.TicketTransferRequest"/>. The automated
/// void+reissue engine was removed when transfers moved to manual processing;
/// the column lingers until a follow-up PR drops it post-prod-soak (see
/// memory/architecture/no-drops-until-prod-verified.md). No code reads it.
/// </summary>
public enum TicketTransferVendorResult
{
    NotAttempted,
    Succeeded,
    VoidSucceededIssueFailed,
    Failed,
}
