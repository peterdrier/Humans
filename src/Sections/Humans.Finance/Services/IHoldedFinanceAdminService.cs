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
    Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default);

    /// <summary>Manually binds a member to an existing Holded creditor account by 400000xx number.
    /// Fails, writing nothing, when the account is already bound or no Holded contact carries it.</summary>
    Task<CreditorBindResult> SetCreditorContactAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Clears the member's creditor binding — the remedy for a wrong bind or a collision. Removes
    /// the whole row, not just the number: the contact id alone merges two members' payables just as
    /// thoroughly. The next push re-resolves. False when nothing was bound.</summary>
    Task<bool> ClearCreditorContactAsync(Guid userId, CancellationToken ct = default);

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
    /// (nobodies-collective/Humans#1185).</summary>
    Task<(IReadOnlyList<SepaPayoutTransferRow> Rows,
          string? UnavailableReason,
          IReadOnlyList<SepaBankMovementVm> UnmatchedMovements,
          string? BankFeedError)>
        GetSepaPayoutsAsync(CancellationToken ct = default);

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
        Guid transferId, string bankMovementId, Guid? actorUserId);

    /// <summary>Books every unbooked transfer of one payout file against the single Sabadell line
    /// the bank debited the whole file as — the line's amount must be the file's total, exactly.
    /// Only ever a finance admin's Process click, never the sweep. Each transfer books as
    /// <see cref="BookSepaTransferAsync"/> would (documents oldest first, remainder as a journal
    /// entry, dated the line); the line is reconciled once, after the last. Every transfer is
    /// checked before any is posted; a Holded refusal part-way leaves the booked ones booked, and
    /// processing the file again finishes the rest. Takes no <c>CancellationToken</c>, for the same
    /// reason.</summary>
    [ExternalWrite]
    Task<SepaBookingResult> BookSepaFileAsync(Guid fileId, string bankMovementId, Guid actorUserId);
}
