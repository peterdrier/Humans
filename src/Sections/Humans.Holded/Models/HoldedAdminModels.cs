using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Holded.Models;

/// <summary>The /Holded page: this section's overview plus the two things only Finance knows —
/// how its purchase-doc sync is doing and how many creditor bindings exist.</summary>
/// <param name="CreditorBindings">Repo-backed count from Finance — no live Holded read.</param>
internal sealed record HoldedOverviewVm(
    HoldedAdminOverview Overview, HoldedDocSyncInfo DocSync, int? CreditorBindings);

/// <summary>
/// Everything the /Holded screen shows about the mirror itself. Assembled from the cache plus
/// one live call (GET /usage); Finance's doc-sync row and creditor-binding count are joined on
/// by the controller, because the Holded section never references Finance.
/// </summary>
internal sealed record HoldedAdminOverview(
    bool ApiReachable,
    HoldedUsageDto? Usage,
    int MonthlyCallBudget,
    IReadOnlyList<HoldedMonthlyCalls> CallsByMonth,
    IReadOnlyList<HoldedSyncStateRow> SyncStates,
    IReadOnlyList<HoldedAccountRow> Accounts,
    int LedgerLineCount);

/// <summary>Metered API calls for one Madrid-zone month.</summary>
internal sealed record HoldedMonthlyCalls(
    int Year, int Month, int Total, IReadOnlyDictionary<string, int> ByEndpoint);

internal sealed record HoldedSyncStateRow(
    string Kind, string Status, Instant? LastSyncAt, string? LastError, int LastCount);

/// <summary>One chart account on the /Holded reconciliation table.</summary>
/// <param name="Group">The PGC group's English name, derived from the account number's leading
/// digit — not Holded's own <c>group</c> string, which is Spanish ("Existencias", "Activo no
/// corriente"). The number is the authority: it is what the group means.</param>
/// <param name="LocalBalance">Null when the mirror holds no line for the account — distinct
/// from a cached zero, which reconciles a zero Holded balance.</param>
/// <param name="HoldedHasPostings">Holded's own debit or credit total is non-zero. Not derivable
/// from <paramref name="HoldedBalance"/>: an account with equal debits and credits — a clearing
/// account, a bank drained to nothing — nets to zero while having been posted to all year.</param>
internal sealed record HoldedAccountRow(
    int Number, string Name, string? Group,
    decimal HoldedBalance, decimal? LocalBalance, int LocalLineCount, bool Reconciled,
    bool HoldedHasPostings);

/// <summary>
/// One general-ledger account and every cached line on it, in Holded's own sign convention
/// (debit and credit columns, balance = Σdebit − Σcredit) so the page reads the same as Holded's
/// UI. Finance inverts for its creditor pages; this one never does.
/// </summary>
/// <param name="Lines">Date first, then entry/line — the order the daybook was written in.</param>
internal sealed record HoldedAccountStatement(
    HoldedAccountRow Account, IReadOnlyList<HoldedLedgerLineInfo> Lines);
