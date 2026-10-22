using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Models;

namespace Humans.Finance.Services;

/// <summary>Only Finance's own screens call this; the cross-section surface is <see cref="Contracts.IHoldedFinanceService"/>.</summary>
internal interface IHoldedFinanceAdminService : IApplicationService
{
    /// <summary>Every active-year budget category with its mapped, proposed or orphaned account, numbered from
    /// <paramref name="blockStart"/>.</summary>
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);

    /// <summary>Additive only.</summary>
    [ExternalWrite]
    Task<int> ProvisionAsync(int blockStart, bool addAll, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Writes nothing when the account is already bound or no Holded contact carries it.</summary>
    Task<CreditorBindResult> SetCreditorContactAsync(
        Guid userId, int supplierAccountNum, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Removes the whole row, not just the number: the contact id alone merges two members' payables too.
    /// The next push re-resolves. False when nothing was bound.</summary>
    Task<bool> ClearCreditorContactAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Local cache only, so the page cannot inherit the connector's timeout.</summary>
    Task<HoldedConnectorVm> GetConnectorOverviewAsync(CancellationToken ct = default);

    /// <summary>Read on every /Finance/Creditors load so the page says why payout is unavailable.</summary>
    SepaPayoutSettings GetSepaPayoutSettings();

    /// <summary>All-or-nothing: any bad row refuses the whole generation. Nothing is stamped on a report or member:
    /// settlement closes through Holded and the next ledger sync. The caller has validated
    /// <paramref name="maxPerTransfer"/> is positive.</summary>
    Task<SepaPayoutResult> GenerateSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>One live bank-feed call; a vendor failure costs the candidates and the panel, not the page.
    /// <para><paramref name="toleranceDays"/> is how far a bank line may sit from its file's date: either side for a
    /// textless line (the date is half of what ties it), before only for a line naming the account. The same value
    /// rides every booking posted from the view, so the booking re-validates under the window the page paired
    /// under.</para></summary>
    Task<(IReadOnlyList<SepaPayoutTransferRow> Rows,
          string? UnavailableReason,
          IReadOnlyList<SepaBankMovementVm> UnmatchedMovements,
          string? BankFeedError)>
        GetSepaPayoutsAsync(int toleranceDays = DefaultSepaMatchToleranceDays, CancellationToken ct = default);

    /// <summary>Also the slack the sweep reads the feed back by.</summary>
    const int DefaultSepaMatchToleranceDays = 2;

    /// <summary>Re-validates the pairing, pays open documents oldest first (dated the bank line), settles any
    /// remainder as one journal entry, stamps the row and reconciles. A run Holded refuses mid-way is retryable and
    /// posts only the difference. A null <paramref name="actorUserId"/> is the sweep. No <c>CancellationToken</c> on
    /// purpose: a booking that has posted a payment must run to the end.</summary>
    [ExternalWrite]
    Task<SepaBookingResult> BookSepaTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId,
        int toleranceDays = DefaultSepaMatchToleranceDays);

    /// <summary>The line's amount must be the file's total, exactly. Only a finance admin's Process click, never the
    /// sweep. Every transfer is checked before any is posted; a refusal part-way leaves booked ones booked, and a rerun
    /// finishes. No <c>CancellationToken</c>, for the same reason.</summary>
    [ExternalWrite]
    Task<SepaBookingResult> BookSepaFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId,
        int toleranceDays = DefaultSepaMatchToleranceDays);

    /// <summary>For lines Sabadell booked one per transfer with no remittance text, matched on amount and date alone.
    /// Refuses the whole file unless every unbooked transfer has exactly one line. Only a finance admin's click; a
    /// refusal part-way leaves booked ones booked. No <c>CancellationToken</c>, for the same reason.</summary>
    [ExternalWrite]
    Task<SepaBookingResult> BookSepaFileMatchesAsync(
        Guid fileId, Guid actorUserId, int toleranceDays = DefaultSepaMatchToleranceDays);

    /// <summary>Flagged, never deleted. Refused once the row is booked, tied to a bank line or has posted a payment.
    /// Audits and tells the member.</summary>
    Task<SepaBookingResult> WithdrawSepaTransferAsync(Guid transferId, Guid actorUserId, CancellationToken ct = default);

    Task<HoldedDuplicatesVm> GetHoldedDuplicatesAsync(CancellationToken ct = default);

    /// <summary>Recomputed when it runs: documents first, then contacts left empty, one audit entry per id, then a
    /// ledger sync. No <c>CancellationToken</c>: a run that has deleted one document finishes the rest.</summary>
    [ExternalWrite]
    Task<HoldedDuplicateApplyResult> RemoveHoldedDuplicatesAsync(Guid actorUserId);
}
