using Humans.Base.Interfaces;
using Humans.Finance.Models;

namespace Humans.Finance.Services;

/// <summary>
/// The <c>/Finance/Holded</c> screen's read model. Internal: the screen lives in this section, so
/// nothing outside it consumes this — the cross-section surface stays
/// <see cref="Contracts.IHoldedFinanceService"/>. Mirrors the Holded section's own
/// <c>IHoldedAdminService</c>, for the same reason.
/// </summary>
internal interface IHoldedFinanceAdminService : IApplicationService
{
    /// <summary>Everything <c>/Finance/Holded</c> renders, from the local cache only — no Holded
    /// HTTP call, so the page cannot inherit the connector's timeout
    /// (nobodies-collective/Humans#976).</summary>
    Task<HoldedConnectorVm> GetConnectorOverviewAsync(CancellationToken ct = default);

    /// <summary>Whether the organisation's SEPA identity is configured, and the per-transfer cap.
    /// Read on every /Finance/Creditors load so the page can say why payout is unavailable rather
    /// than offering a button that always fails.</summary>
    SepaPayoutSettings GetSepaPayoutSettings();

    /// <summary>Validates the selection, persists the payout record and returns the pain.001.001.09
    /// file. All-or-nothing: any bad row refuses the whole generation. Nothing is stamped onto a
    /// report or a member — settlement closes through Holded and the next ledger sync.
    /// <paramref name="maxPerTransfer"/> is the cap entered on the screen for this batch; the caller
    /// has already validated it is positive.</summary>
    Task<SepaPayoutResult> GenerateSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, Guid actorUserId,
        CancellationToken ct = default);
}
