using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Models;

namespace Humans.Finance.Services;

/// <summary>
/// What only Finance's own <c>/Finance/*</c> screens call. Internal: the screens live in this
/// section, so nothing outside it consumes this — the cross-section surface stays
/// <see cref="Contracts.IHoldedFinanceService"/>. Mirrors the Holded section's own
/// <c>IHoldedAdminService</c>, for the same reason.
/// </summary>
internal interface IHoldedFinanceAdminService : IApplicationService
{
    /// <summary>The <c>/Finance/HoldedAccounts</c> plan: every active-year budget category with its
    /// mapped, proposed or orphaned Holded expense account, numbered from <paramref name="blockStart"/>.</summary>
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);

    /// <summary>Creates the plan's missing accounts in Holded and maps them; additive only.</summary>
    [ExternalWrite]
    Task<int> ProvisionAsync(int blockStart, bool addAll, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Manually binds a member to an existing Holded creditor account by 400000xx number.
    /// Fails, writing nothing, when the account is already bound or no Holded contact carries it.</summary>
    Task<CreditorBindResult> SetCreditorContactAsync(
        Guid userId, int supplierAccountNum, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Clears the member's creditor binding — the remedy for a wrong bind or a collision. Removes
    /// the whole row, not just the number: the contact id alone merges two members' payables just as
    /// thoroughly. The next push re-resolves. False when nothing was bound.</summary>
    Task<bool> ClearCreditorContactAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);

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

    /// <summary>Every generated transfer with its booking state, the reason it cannot be booked and
    /// the Sabadell line that would settle it — plus the one reason booking is off for the whole
    /// screen (missing configuration), the outgoing bank lines that matched nothing or matched
    /// ambiguously, and the reason the live bank feed could not be read. Makes one live bank-feed
    /// call; a vendor failure costs the candidates and the panel, not the page
    /// (nobodies-collective/Humans#1185).
    /// <para><paramref name="toleranceDays"/> is how far a bank line's date may sit from its
    /// file's generation date and still be that file's: either side for a textless line (a
    /// Sabadell line names no account, so the date is half of what ties it to a transfer), before
    /// only for a line whose text names the account. The page's default is
    /// <see cref="DefaultSepaMatchToleranceDays"/>; the treasurer widens it per view, and the same
    /// value rides every booking posted from that view so the booking re-validates with the window
    /// the page paired under.</para></summary>
    Task<(IReadOnlyList<SepaPayoutTransferRow> Rows,
          string? UnavailableReason,
          IReadOnlyList<SepaBankMovementVm> UnmatchedMovements,
          string? BankFeedError)>
        GetSepaPayoutsAsync(int toleranceDays = DefaultSepaMatchToleranceDays, CancellationToken ct = default);

    /// <summary>The default for <c>toleranceDays</c> on the SEPA page and every booking: a line
    /// within two days of its file's generation date. Also the slack the sweep reads the feed back
    /// by.</summary>
    const int DefaultSepaMatchToleranceDays = 2;

    /// <summary>Books one transfer against the outgoing Sabadell line that paid it
    /// (nobodies-collective/Humans#1185): re-validates the pairing server-side, reads the live
    /// creditor balance and what is already posted under this transfer's tag, pays the member's
    /// open Holded purchase documents oldest first (dated the bank line), settles any remainder as
    /// one journal entry, stamps the row and reconciles the bank line. A run that Holded refuses
    /// mid-way writes nothing and is retryable — the next attempt posts only the difference.
    /// <paramref name="actorUserId"/> null means the sweep booked it, and the audit entry carries
    /// the job name instead.
    /// Takes no <c>CancellationToken</c> on purpose: a booking that has posted one payment to Holded
    /// must run to the end regardless of whether the admin is still watching
    /// (<c>memory/architecture/cancellation-token-propagation.md</c>).</summary>
    [ExternalWrite]
    Task<SepaBookingResult> BookSepaTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId,
        int toleranceDays = DefaultSepaMatchToleranceDays);

    /// <summary>Books every unbooked transfer of one payout file against the single Sabadell line
    /// the bank debited the whole file as — the line's amount must be the file's total, exactly.
    /// Only ever a finance admin's Process click, never the sweep. Each transfer books as
    /// <see cref="BookSepaTransferAsync"/> would (documents oldest first, remainder as a journal
    /// entry, dated the line); the line is reconciled once, after the last. Every transfer is
    /// checked before any is posted; a Holded refusal part-way leaves the booked ones booked, and
    /// processing the file again finishes the rest. Takes no <c>CancellationToken</c>, for the same
    /// reason.</summary>
    [ExternalWrite]
    Task<SepaBookingResult> BookSepaFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId,
        int toleranceDays = DefaultSepaMatchToleranceDays);

    /// <summary>Books every unbooked transfer of one payout file, each against the Sabadell line
    /// <see cref="GetSepaPayoutsAsync"/> paired it with — the approval for lines Sabadell booked one
    /// per transfer with no remittance text, matched on amount and date alone. Refuses the whole
    /// file, posting nothing, unless every unbooked transfer in it has exactly one line; each then
    /// books as <see cref="BookSepaTransferAsync"/> would, re-validating its own pairing. Only ever
    /// a finance admin's click, never the sweep. A refusal part-way leaves the booked ones booked,
    /// and approving the file again finishes the rest. Takes no <c>CancellationToken</c>, for the
    /// same reason.</summary>
    [ExternalWrite]
    Task<SepaBookingResult> BookSepaFileMatchesAsync(
        Guid fileId, Guid actorUserId, int toleranceDays = DefaultSepaMatchToleranceDays);

    /// <summary>Withdraws one transfer the bank cancelled by hand before execution: the row is
    /// flagged, never deleted, and drops out of every matcher, booking and payability gate. Refused
    /// once the row is booked, tied to a bank line or has posted a payment. Writes one audit entry
    /// and tells the member.</summary>
    Task<SepaBookingResult> WithdrawSepaTransferAsync(Guid transferId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The <c>/Finance/HoldedDuplicates</c> dry run: every expense line whose Holded
    /// document the outbox race created twice, what Apply would delete and what it refuses.</summary>
    Task<HoldedDuplicatesVm> GetHoldedDuplicatesAsync(CancellationToken ct = default);

    /// <summary>Deletes the duplicates the dry run names, recomputed at the moment it runs: purchase
    /// documents first, then the contacts left empty, one audit entry per id; then a ledger sync, with
    /// each creditor balance read live before and after. Takes no <c>CancellationToken</c>: a run
    /// that has deleted one document finishes the rest.</summary>
    [ExternalWrite]
    Task<HoldedDuplicateApplyResult> RemoveHoldedDuplicatesAsync(Guid actorUserId);
}
